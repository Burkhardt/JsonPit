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
	/// <summary>
	/// Quick check: is this exact process currently recorded as the master owner?
	/// For unflagged pits this always returns true.
	/// Note: this does NOT verify ticket expiration — use <see cref="TryAcquireMaster"/> for that.
	/// </summary>
	public bool RunningOnMaster() =>
		unflagged || MasterFlag().Originator == ExactProcessIdentity;
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
	public ProcessFlagFile ProcessFlag()
	{
		fileFlag ??= new ProcessFlagFile(PitDir, processIdentity);
		if (fileFlag.Lines.Count == 0) // just created
			fileFlag.Update();
		return fileFlag;
	}
	private ProcessFlagFile fileFlag;
	/// <summary>
	/// Expires the current process activity window when this instance created one
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
	public MasterFlagFile MasterFlag()
	{
		masterFlag = new MasterFlagFile(PitDir, "Master");
		if (string.IsNullOrEmpty(masterFlag.Originator))
			masterFlag.Update(originator: ExactProcessIdentity);
		return masterFlag;
	}
	private MasterFlagFile masterFlag;
	/// <summary>
	/// Hook invoked after every <see cref="TryAcquireMaster"/> outcome so derived classes
	/// can track exact-process master tenures and detect live authority transfers.
	/// </summary>
	/// <param name="acquired">Whether this exact process now holds the master lease.</param>
	/// <param name="recordedOwner">The owner recorded in Master.flag (may be this process).</param>
	/// <param name="ownerLeaseValid">Whether the recorded owner's lease is currently valid.</param>
	protected virtual void OnMasterAuthorityEvaluated(bool acquired, string recordedOwner, bool ownerLeaseValid) { }
	/// <summary>
	/// Attempts to acquire master rights under the agreed v3.13.2 exact-process ownership
	/// contract (CR003):
	/// <list type="number">
	///   <item>Only the exact owning process (matching PID-specific identity) may renew its active master lease directly.</item>
	///   <item>A different PID with the same stable participant must not inherit master authority while the
	///   recorded owner's process window remains active — it follows the non-master change-file path.</item>
	///   <item>When the recorded owner's process window has been explicitly released or has expired, a new PID
	///   with the same stable participant may inherit the still-protected participant lease.</item>
	///   <item>An expired lease may be claimed by any participant unless a foreign process was recently active.</item>
	/// </list>
	/// PID-level collision detection and safe handover are JsonPit responsibilities; callers
	/// do not invent unique subscribers merely to distinguish PIDs. Unflagged pits always return true.
	/// </summary>
	public bool TryAcquireMaster()
	{
		if (unflagged) return true;
		var master = MasterFlag();
		// Fast path: this exact process already owns a valid lease — renew it.
		if (master.IsOwnedBy(ExactProcessIdentity))
		{
			master.TryClaim(ExactProcessIdentity);  // refresh the timestamp
			OnMasterAuthorityEvaluated(true, ExactProcessIdentity, true);
			return true;
		}
		if (!master.IsExpired)
		{
			var recordedOwner = master.Originator;
			var ownerParticipant = MasterFlagFile.ParticipantOf(recordedOwner);
			if (ownerParticipant == ParticipantIdentity)
			{
				// Same stable participant, different (or legacy pre-PID) process identity.
				var ownerIsExact = MasterFlagFile.IsExactProcessIdentity(recordedOwner);
				if (ownerIsExact && ProcessFlagFile.IsProcessWindowActive(PitDir, recordedOwner))
				{
					// Recorded owner's process window is still active — change-file path.
					OnMasterAuthorityEvaluated(false, recordedOwner, true);
					return false;
				}
				// Owner released/expired its window — inherit the still-protected participant lease.
				master.Update(originator: ExactProcessIdentity);
				OnMasterAuthorityEvaluated(true, ExactProcessIdentity, true);
				return true;
			}
			// A different participant holds a valid lease — we cannot claim.
			OnMasterAuthorityEvaluated(false, recordedOwner, true);
			return false;
		}
		// Ticket is expired — but is anybody else actively writing?
		if (AnyForeignProcessActive())
		{
			OnMasterAuthorityEvaluated(false, master.Originator, false);
			return false;
		}
		// Nobody active + ticket expired → claim it.
		var claimed = master.TryClaim(ExactProcessIdentity);
		OnMasterAuthorityEvaluated(claimed, claimed ? ExactProcessIdentity : master.Originator, claimed);
		return claimed;
	}
	/// <summary>
	/// Scans all *.flag files in PitDir (excluding Master.flag and same-machine process flags)
	/// and returns true if another machine/process wrote its flag within <see cref="MasterFlagFile.TicketDuration"/>.
	/// Each process activity flag is named "{MachineName}-{AppName}-{PID}.flag".
	/// Same-machine flags are excluded because the stable master ticket coordinates writers there.
	/// </summary>
	private bool AnyForeignProcessActive()
	{
		if (!PitDir.Exists()) return false;
		var now = DateTimeOffset.UtcNow;
		var myFlagName = ProcessFlagFile.CurrentFlagName(processIdentity);
		foreach (var flagRaiFile in PitDir.EnumerateFiles("*.flag"))
		{
			// Skip Master.flag — that's the ticket, not a process flag
			if (flagRaiFile.Name.Equals("Master", StringComparison.OrdinalIgnoreCase))
				continue;
			// Skip our own process flag
			if (flagRaiFile.Name.Equals(myFlagName, StringComparison.OrdinalIgnoreCase))
				continue;
			var separator = flagRaiFile.Name.IndexOf('-');
			if (separator > 0 && flagRaiFile.Name[..separator].Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
				continue;
			var flag = new TextFile(flagRaiFile.FullName);
			flag.Read();
			if (flag.Lines is not { Count: > 0 }) continue;
			var tv = new TimestampedValue(flag.Lines[0]);
			if ((now - tv.Time) <= MasterFlagFile.TicketDuration)
				return true;    // foreign process was active recently
		}
		return false;
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
		MasterFlag().Time.UtcTicks > ProcessFlag().Time.UtcTicks;
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
