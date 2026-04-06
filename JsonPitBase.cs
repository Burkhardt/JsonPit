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
		get => version ??= System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(2);
		set => version = value;
	}
	private static string version;
	public static RaiPath ConfigDefaultDir
	{
		get => configDirDefault ??= Os.CloudStorageRootDir / "Config";
		set => configDirDefault = value;
	}
	private static RaiPath configDirDefault;
	public static Options jilOptions = new(prettyPrint: true, excludeNulls: false, jsonp: false,
		dateFormat: DateTimeFormat.ISO8601, includeInherited: true);
	#region Semaphore
	protected int usingPersistence = 0;
	protected readonly object _locker = new();
	#endregion
	#region Flag file
	/// <summary>
	/// Quick check: does this machine currently appear as the master originator?
	/// For unflagged pits this always returns true.
	/// Note: this does NOT verify ticket expiration — use <see cref="TryAcquireMaster"/> for that.
	/// </summary>
	public bool RunningOnMaster() =>
		unflagged || MasterFlag().Originator == Environment.MachineName;
	protected bool unflagged;
	public ProcessFlagFile ProcessFlag()
	{
		fileFlag ??= new ProcessFlagFile(PitDir, processIdentity);
		if (fileFlag.Lines.Count == 0) // just created
			fileFlag.Update();
		return fileFlag;
	}
	private ProcessFlagFile fileFlag;
	/// <summary>
	/// Application identity for the process flag filename, e.g. "pits", "RAIkeep", "Nomsa".
	/// Set from Pit's Subscriber. Falls back to OS process name if null.
	/// Result: "{MachineName}-{processIdentity}.flag", e.g. "ubuntu-pits.flag".
	/// </summary>
	protected string processIdentity;
	public MasterFlagFile MasterFlag()
	{
		masterFlag = new MasterFlagFile(PitDir, "Master");
		if (string.IsNullOrEmpty(masterFlag.Originator))
			masterFlag.Update();
		return masterFlag;
	}
	private MasterFlagFile masterFlag;
	/// <summary>
	/// Attempts to acquire master rights using the timed-ticket protocol:
	/// <list type="number">
	///   <item>a) Scan all *.flag files to see if anybody was active within <see cref="MasterFlagFile.TicketDuration"/>.</item>
	///   <item>b) Check if the ticket in Master.flag has expired, or if we already own it.</item>
	///   <item>c) If the ticket expired (and no other process is active, or we're already master), claim it.</item>
	///   <item>d) Return true only if we now hold a valid master ticket.</item>
	/// </list>
	/// Unflagged pits always return true.
	/// </summary>
	public bool TryAcquireMaster()
	{
		if (unflagged) return true;
		var master = MasterFlag();
		// Fast path: we already own a valid ticket — renew it
		if (master.IsOwnedByMe)
		{
			master.TryClaim();  // refresh the timestamp
			return true;
		}
		// If the ticket is still valid and somebody else owns it, we can't claim
		if (!master.IsExpired)
			return false;
		// Ticket is expired — but is anybody else actively writing?
		// a) scan all process flag files; if any foreign process was active within TicketDuration, back off
		if (AnyForeignProcessActive())
			return false;
		// c) Nobody active + ticket expired → claim it
		return master.TryClaim();
	}
	/// <summary>
	/// Scans all *.flag files in PitDir (excluding Master.flag and our own process flag)
	/// and returns true if any other machine/process wrote its flag within <see cref="MasterFlagFile.TicketDuration"/>.
	/// Each flag file is named "{MachineName}-{AppName}.flag", so different apps on the
	/// same machine are treated as separate participants.
	/// </summary>
	private bool AnyForeignProcessActive()
	{
		if (!PitDir.Exists()) return false;
		var now = DateTimeOffset.UtcNow;
		var myFlagName = ProcessFlagFile.FlagName(processIdentity);
		foreach (var flagRaiFile in PitDir.EnumerateFiles("*.flag"))
		{
			// Skip Master.flag — that's the ticket, not a process flag
			if (flagRaiFile.Name.Equals("Master", StringComparison.OrdinalIgnoreCase))
				continue;
			// Skip our own process flag
			if (flagRaiFile.Name.Equals(myFlagName, StringComparison.OrdinalIgnoreCase))
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
	public virtual DateTimeOffset GetFileChanged()
	{
		// TODO: Rainer — consider adding LastWriteTimeUtc to RaiFile so we don't need System.IO.FileInfo here
		var info = new System.IO.FileInfo(JsonFile.FullName);
		return info.LastWriteTimeUtc;
	}
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
	/// Changes from other servers are available when foreign change files exist.
	/// </summary>
	public bool ForeignChangesAvailable() =>
		EnumerateChangeFiles()
			.Any(cf => !cf.Name.EndsWith("_" + Environment.MachineName, StringComparison.OrdinalIgnoreCase));
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
