using Jil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OsLib;
namespace JsonPit;
/// <summary>
/// Common base for pits with config, flags, and persistence helpers.
/// </summary>
public class JsonPitBase
{
	public static string Version
	{
		get => field ??= System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(2);
		set => field = value;
	}
	public static Options jilOptions = new(prettyPrint: true, excludeNulls: false, jsonp: false,
		dateFormat: DateTimeFormat.ISO8601, includeInherited: true);
	#region Semaphore
	protected int usingPersistence = 0;
	protected readonly object _locker = new();
	#endregion
	#region Flag file
	/// <summary>Whether this instance holds its unexpired write turn.</summary>
	public bool RunningOnMaster() => OwnsClaim();
	protected DateTimeOffset lastLoadedUtc;
	protected bool unflagged;
	/// <summary>
	/// Stable participant identity: "{MachineName}-{Subscriber}". Meaningful and stable
	/// across process restarts; it does not distinguish concurrent PIDs.
	/// </summary>
	public string ParticipantIdentity => ProcessFlagFile.FlagName(processIdentity);
	/// <summary>
	/// Exact process identity: the PID-specific process-flag stem
	/// "{MachineName}-{Subscriber}-{PID}" (CR003, coordinated v3.13.2). Master ownership
	/// and change-file authorship record this exact identity; only the exact owning
	/// process may renew its active master lease directly.
	/// </summary>
	public string ExactProcessIdentity => ProcessFlagFile.CurrentFlagName(processIdentity);
	public ProcessFlagFile ProcessFlag() => fileFlag ??= new ProcessFlagFile(PitDir, processIdentity);
	private ProcessFlagFile fileFlag;
	/// <summary>
	/// Deletes the current process activity window when this instance created one
	/// and the flag is still owned by the current OS process.
	/// This does not release or modify the master writer ticket.
	/// </summary>
	public bool TryReleaseProcessWindow() =>
		!unflagged && fileFlag is not null && fileFlag.TryReleaseCurrentProcess();
	/// <summary>
	/// Application identity used in the process activity flag filename and stable master-ticket identity,
	/// e.g. "pits", "RAIkeep", "Nomsa".
	/// Set from Pit's Subscriber. Falls back to OS process name if null.
	/// Process activity filename: "{MachineName}-{processIdentity}-{PID}.flag".
	/// </summary>
	protected string processIdentity;
	public MasterFlagFile MasterFlag() => new(PitDir, "Master");
	/// <summary>
	/// Hook invoked after every <see cref="TryAcquireMaster"/> outcome so derived classes
	/// can track exact-process master tenures and detect live authority transfers.
	/// </summary>
	/// <param name="acquired">Whether this exact process now holds the master lease.</param>
	/// <param name="recordedOwner">The owner recorded in Master.flag (may be this process).</param>
	/// <param name="ownerLeaseValid">Whether the recorded owner's lease is currently valid.</param>
	protected virtual void OnMasterAuthorityEvaluated(bool acquired, string recordedOwner, bool ownerLeaseValid) { }
	private readonly object claimGate = new();
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, string Writer), DateTimeOffset> bans = new();
	private (string, string) CooldownKey => (JsonFile.FullName, JsonFile.Path.Cloud ? Environment.MachineName : ExactProcessIdentity);
	private PitTiming claimTiming;
	protected PitTiming Timing => claimTiming ?? PitTiming.Load(JsonFile.Path);
	protected DateTimeOffset claimTime;
	public (bool Acquired, DateTimeOffset EarliestWriteUtc) TryAcquireMaster()
	{
		if (!Monitor.TryEnter(claimGate)) return (false, default);
		try
		{
			var turn = ReadOnly || unflagged || JsonFile.Path.Cloud ? ClaimAvailableTicket() : ClaimLocalTicket();
			if (turn.Acquired && !unflagged) OnMasterAuthorityEvaluated(true, ExactProcessIdentity, true);
			return turn;
		}
		finally { Monitor.Exit(claimGate); }
	}

	private (bool Acquired, DateTimeOffset EarliestWriteUtc) ClaimLocalTicket()
	{
		// Serialize the ticket read and replacement across processes. Contenders never queue.
		var pathBytes = System.Text.Encoding.UTF8.GetBytes(JsonFile.FullName.ToUpperInvariant());
		var pathHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pathBytes));
		using var gate = new Mutex(false, "JsonPitClaim-" + pathHash);
		try { if (!gate.WaitOne(0)) return (false, default); }
		catch (AbandonedMutexException) { /* Ownership transfers to this thread. */ }
		try { return ClaimAvailableTicket(); }
		finally { gate.ReleaseMutex(); }
	}

	private (bool Acquired, DateTimeOffset EarliestWriteUtc) ClaimAvailableTicket()
	{
		if (ReadOnly) return (false, default);
		if (unflagged) return (true, DateTimeOffset.UtcNow);
		var master = MasterFlag();
		MasterTicket previous;
		try { previous = master.ReadTicket(); }
		catch (Exception ex) when (ex is FormatException or System.IO.IOException) { return (false, default); }
		var timing = PitTiming.Load(JsonFile.Path);
		var window = timing.PitSync + timing.Buffer;
		var now = DateTimeOffset.UtcNow;
		bool cloud = JsonFile.Path.Cloud;
		bool previousWriter = cloud
			? string.Equals(previous?.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
			: previous?.Owner == ExactProcessIdentity;
		if (previous is { State: "Held" } && now < previous.Claimed + timing.Ticket ||
			previous is { State: "Returned" } && previousWriter && now < previous.Written + window ||
			bans.TryGetValue(CooldownKey, out var until) && now < until)
			return (false, default);
		var pitReady = previous switch
		{
			null => now,
			{ State: "Returned", Written: { } written } => (written < now ? written : now) + window,
			_ => now + window
		};
		var flagReady = now + timing.FlagSync + timing.Buffer;
		master.WriteTicket(new("Held", ExactProcessIdentity, now, previous?.Written, previous?.Machine ?? ""));
		claimTiming = timing;
		claimTime = now;
		ProcessFlag().Update(now);
		return (true, cloud ? (pitReady > flagReady ? pitReady : flagReady) : now);
	}

	protected bool OwnsClaim()
	{
		if (unflagged) return true;
		try
		{
			return MasterFlag().ReadTicket() is { State: "Held" } ticket &&
				ticket.Owner == ExactProcessIdentity && ticket.Claimed == claimTime &&
				DateTimeOffset.UtcNow < ticket.Claimed + Timing.Ticket;
		}
		catch (Exception ex) when (ex is FormatException or System.IO.IOException) { return false; }
	}

	protected bool ReturnClaim(DateTimeOffset now)
	{
		if (!OwnsClaim()) return false;
		MasterFlag().WriteTicket(new("Returned", ExactProcessIdentity, claimTime, now, Environment.MachineName));
		lastLoadedUtc = now;
		bans[CooldownKey] = now + Timing.PitSync + Timing.Buffer;
		return true;
	}
	#endregion
	#region Store and load options
	public bool ReadOnly { get; set; }
	public bool Backup { get; set; }
	#endregion
	/// <summary>
	/// Did the master update the file since I last used it?
	/// </summary>
	public bool MasterUpdatesAvailable() =>
		MasterFlag().ReadTicket()?.Written > lastLoadedUtc;
	/// <summary>
	/// Overload this in derived classes to give it some per-item meaning.
	/// </summary>
	public virtual DateTimeOffset GetFileChanged() => JsonFile.LastWriteTimeUtc;
	/// <summary>
	/// Overload this in derived classes to give it some per-item meaning once Infos is defined.
	/// </summary>
	public virtual DateTimeOffset GetMemChanged() => DateTimeOffset.UtcNow; // memory is always newer
	/// <summary>
	/// Checks whether the disk version has newer changes than the in-memory version.
	/// </summary>
	public bool DiskHasNewerChanges() =>
		JsonFile.Exists() && GetFileChanged() > GetMemChanged();
	/// <summary>
	/// Changes from other processes are available when change files exist that were not
	/// authored by this exact process (CR003, coordinated v3.13.2).
	/// </summary>
	public bool ForeignChangesAvailable() =>
		EnumerateChangeFiles()
			.Any(cf =>
			{
				var identity = ChangeFile.IdentityOf(cf.Name);
				return identity is not null &&
					!identity.Equals(ExactProcessIdentity, StringComparison.OrdinalIgnoreCase);
			});
	/// <summary>
	/// Directory where the PitFile, change files, and flag files all live together.
	/// No separate Changes subdirectory — everything sits alongside the pit file.
	/// Created on first access if it doesn't exist yet.
	/// </summary>
	public RaiPath PitDir
	{
		get
		{
			pitDir ??= JsonFile.Path.mkdir();
			return pitDir;
		}
	}
	private RaiPath pitDir;
	protected IEnumerable<TextFile> EnumerateChangeFiles()
	{
		if (!PitDir.Exists())
			return Enumerable.Empty<TextFile>();
		return PitDir.EnumerateFiles("*.json")
			.Where(f => f.Name != JsonFile.Name)   // exclude the pit file itself
			.Select(f => new TextFile(f.FullName));
	}
	/// <summary>
	/// The main PitFile for this pit (directory and file share the same name).
	/// </summary>
	public PitFile JsonFile { get; set; }
	protected bool descending;
	public JsonPitBase(bool readOnly = true, bool backup = false, bool unflagged = false, bool descending = false)
	{
		ReadOnly = readOnly;
		Backup = backup;
		this.unflagged = unflagged;
		this.descending = descending;
	}
}
