using System;
using System.Collections.Generic;
using System.Linq;
using OsLib;
namespace JsonPit;
/// <summary>
/// Flag file tracking which participant currently holds master rights for a pit.
/// Stored as a single-line text file: "OwnerIdentity|ISO8601-timestamp".
/// The timestamp acts as a timed ticket — master rights expire after <see cref="TicketDuration"/>.
/// </summary>
public class MasterFlagFile : TextFile
{
	/// <summary>How long a master ticket stays valid before it expires.</summary>
	public static TimeSpan TicketDuration { get; set; } = TimeSpan.FromSeconds(60);
	public int mv(MasterFlagFile src, bool replace = false, bool keepBackup = false) =>
		mv((RaiFile)src, replace, keepBackup);
	private readonly object locker = new();
	public new void Save(bool backup = false)
	{
		lock (locker) { base.SaveInPlace(); }
	}
	public string Originator
	{
		get
		{
			if (Lines is not { Count: > 0 }) Read();
			return new TimestampedValue(Lines.Count == 0 ? "|" : Lines[0]).Value;
		}
		set
		{
			Lines = [new TimestampedValue(value).ToString()];
			Changed = true;
			Save();
		}
	}
	public DateTimeOffset Time
	{
		get
		{
			Read();
			return new TimestampedValue(Lines.Count == 0 ? "|" : Lines[0]).Time;
		}
		set
		{
			if (Lines is not { Count: > 0 }) Read();
			var tv = new TimestampedValue(Lines.Count == 0 ? "|" : Lines[0]) { Time = value };
			Lines = [tv.ToString()];
			Changed = true;
			Save();
		}
	}
	/// <summary>
	/// True when the ticket timestamp is older than <see cref="TicketDuration"/> from now.
	/// An empty or missing file is considered expired.
	/// </summary>
	public bool IsExpired
	{
		get
		{
			if (!Exists()) return true;
			Read();
			if (Lines is not { Count: > 0 }) return true;
			var tv = new TimestampedValue(Lines[0]);
			return string.IsNullOrEmpty(tv.Value) || (DateTimeOffset.UtcNow - tv.Time) > TicketDuration;
		}
	}
	/// <summary>
	/// True when this machine currently owns the master ticket and it hasn't expired.
	/// </summary>
	public bool IsOwnedByMe => !IsExpired && Originator == Environment.MachineName;
	/// <summary>
	/// True when the supplied participant identity owns the current master ticket and it hasn't expired.
	/// </summary>
	public bool IsOwnedBy(string originator) => !IsExpired && Originator == originator;
	/// <summary>
	/// Extracts the stable participant identity ("{Machine}-{Subscriber}") from a master
	/// owner value. Since v3.13.2 master ownership records the exact process identity
	/// ("{Machine}-{Subscriber}-{PID}"); stripping the trailing numeric PID segment
	/// yields the participant. A legacy participant-only value is returned unchanged.
	/// </summary>
	public static string ParticipantOf(string ownerIdentity)
	{
		if (string.IsNullOrEmpty(ownerIdentity)) return ownerIdentity;
		var separator = ownerIdentity.LastIndexOf('-');
		if (separator > 0 && separator < ownerIdentity.Length - 1 &&
			ownerIdentity[(separator + 1)..].All(char.IsAsciiDigit))
			return ownerIdentity[..separator];
		return ownerIdentity;
	}
	/// <summary>
	/// True when the recorded owner value names an exact process (contains a PID segment).
	/// </summary>
	public static bool IsExactProcessIdentity(string ownerIdentity) =>
		!string.IsNullOrEmpty(ownerIdentity) && ParticipantOf(ownerIdentity) != ownerIdentity;
	/// <summary>
	/// Enumerates provider-created master-conflict signals in <paramref name="pitDir"/>:
	/// any file matching <c>Master*.flag</c> whose filename is longer than
	/// <c>Master.flag</c>. The suffix is opaque — no provider-specific pattern is parsed.
	/// The exact canonical <c>Master.flag</c> is the authority record and is never
	/// returned here; a longer variant is conflict evidence, not a second authority.
	/// </summary>
	public static IEnumerable<RaiFile> ConflictFlags(RaiPath pitDir)
	{
		if (pitDir is null || !pitDir.Exists()) yield break;
		foreach (var file in pitDir.EnumerateFiles("Master*.flag"))
		{
			if (!file.Name.Equals("Master", StringComparison.Ordinal) &&
				file.Name.StartsWith("Master", StringComparison.Ordinal) &&
				file.NameWithExtension.Length > "Master.flag".Length)
				yield return file;
		}
	}
	/// <summary>
	/// Attempts to claim master rights for the supplied participant identity.
	/// Succeeds when:
	///   - the ticket is expired (no active master), or
	///   - this participant already owns the ticket (renewal).
	/// On success, writes a fresh ticket valid for another <see cref="TicketDuration"/>.
	/// </summary>
	/// <returns>true if this participant now holds the master ticket</returns>
	public bool TryClaim(string originator = null)
	{
		lock (locker)
		{
			var claimant = string.IsNullOrWhiteSpace(originator) ? Environment.MachineName : originator;
			// Re-read from disk — another process may have claimed since our last read
			Read();
			if (!IsExpired && Originator != claimant)
				return false;   // someone else has a valid ticket
			Update(originator: claimant);
			return true;
		}
	}
	public TimestampedValue Update(DateTimeOffset? time = null, string originator = null)
	{
		var tv = new TimestampedValue(Environment.MachineName, DateTimeOffset.UtcNow);
		if (!string.IsNullOrEmpty(originator))
			tv.Value = originator;
		if (time is not null)
			tv.Time = time.Value;
		Lines = [tv.ToString()];
		Changed = true;
		Save();
		return tv;
	}
	public static string FileName(string changeDir, string name) =>
		changeDir + new RaiFile(name).Name + ".flag";
	public MasterFlagFile(RaiPath dir, string name, string server = null)
		: base(dir, name, ext: "flag")
	{
		if (!string.IsNullOrEmpty(server))
			Update(originator: server);
	}
}
/// <summary>
/// Per process activity flag file.
/// Filename: "{MachineName}-{Subscriber}-{PID}.flag", e.g. "Nkosikazi-pits-12345.flag".
/// Content: "{MachineName}:{ProcessName}:{PID}|{ISO8601-timestamp}" for diagnostics.
/// The process-specific filename prevents finite CLI invocations on the same machine
/// from deleting or overwriting each other's activity window.
/// </summary>
public class ProcessFlagFile : MasterFlagFile
{
	/// <summary>
	/// Machine names that are generic defaults — not unique and will cause flag file collisions.
	/// If <see cref="Environment.MachineName"/> matches one of these, <see cref="ValidateMachineName()"/>
	/// logs a warning. See CONFIGURE_SERVER.md for how to set a proper hostname.
	/// </summary>
	private static readonly HashSet<string> GenericMachineNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"localhost", "ubuntu", "debian", "raspberrypi", "default", "docker",
		"buildkitsandbox", "runner", "codespaces", "devcontainer",
		"DESKTOP-", "WIN-", "ip-", "vm-"
	};
	/// <summary>
	/// Returns true if the machine name looks like a proper hostname.
	/// Returns false and writes to Console.Error if it's generic or suspiciously short.
	/// </summary>
	public static bool ValidateMachineName()
	{
		return ValidateMachineName(Environment.MachineName);
	}
	public static bool ValidateMachineName(string machineName)
	{
		var name = machineName;
		if (string.IsNullOrWhiteSpace(name) || name.Length < 3)
		{
			Console.Error.WriteLine($"[JsonPit] WARNING: MachineName '{name}' is too short to be unique. " +
				"Flag file collisions will occur. See CONFIGURE_SERVER.md.");
			return false;
		}
		// Check exact matches
		if (GenericMachineNames.Contains(name))
		{
			Console.Error.WriteLine($"[JsonPit] WARNING: MachineName '{name}' is a generic default. " +
				"Flag file collisions will occur. See CONFIGURE_SERVER.md.");
			return false;
		}
		// Check prefix matches (DESKTOP-XXXXXXX, WIN-XXXXXXX, ip-172-31-x-x, vm-xxxxxx)
		foreach (var prefix in GenericMachineNames.Where(g => g.EndsWith('-')))
		{
			if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				Console.Error.WriteLine($"[JsonPit] WARNING: MachineName '{name}' looks auto-generated. " +
					"Consider setting a memorable hostname. See CONFIGURE_SERVER.md.");
				return false;
			}
		}
		return true;
	}
	public int mv(ProcessFlagFile src, bool replace = false, bool keepBackup = false) =>
		mv((RaiFile)src, replace, keepBackup);
	/// <summary>
	/// Full diagnostic identity: "{MachineName}:{ProcessName}:{PID}".
	/// Written as flag file content so you can see *where* and *what* is running.
	/// </summary>
	public static string CurrentProcessId()
	{
		var p = System.Diagnostics.Process.GetCurrentProcess();
		return $"{Environment.MachineName}:{p.ProcessName}:{p.Id}";
	}
	/// <summary>
	/// Builds the flag file name: "{MachineName}-{subscriber}".
	/// Falls back to "{MachineName}-{ProcessName}" when no subscriber is given.
	/// </summary>
	public static string FlagName(string subscriber = null) =>
		$"{Environment.MachineName}-{subscriber ?? System.Diagnostics.Process.GetCurrentProcess().ProcessName}";
	/// <summary>
	/// Builds the process-specific activity flag name for the current process.
	/// </summary>
	public static string CurrentFlagName(string subscriber = null) =>
		$"{FlagName(subscriber)}-{Environment.ProcessId}";
	/// <summary>
	/// True when the process window recorded for <paramref name="exactProcessIdentity"/>
	/// (its "{Machine}-{Subscriber}-{PID}.flag" activity file in <paramref name="pitDir"/>)
	/// is still active — the file exists and its timestamp lies within
	/// <see cref="MasterFlagFile.TicketDuration"/>. A missing, released (tombstoned), or
	/// aged-out flag means the window has ended.
	/// </summary>
	public static bool IsProcessWindowActive(RaiPath pitDir, string exactProcessIdentity)
	{
		if (pitDir is null || string.IsNullOrEmpty(exactProcessIdentity)) return false;
		var flagFile = new RaiFile(pitDir, exactProcessIdentity, "flag");
		if (!flagFile.Exists()) return false;
		var flag = new TextFile(flagFile.FullName);
		flag.Read();
		if (flag.Lines is not { Count: > 0 }) return false;
		var tv = new TimestampedValue(flag.Lines[0]);
		return (DateTimeOffset.UtcNow - tv.Time) <= MasterFlagFile.TicketDuration;
	}
	/// <summary>
	/// Returns the last recorded window timestamp for <paramref name="exactProcessIdentity"/>,
	/// or null when no window flag exists.
	/// </summary>
	public static DateTimeOffset? ProcessWindowTime(RaiPath pitDir, string exactProcessIdentity)
	{
		if (pitDir is null || string.IsNullOrEmpty(exactProcessIdentity)) return null;
		var flagFile = new RaiFile(pitDir, exactProcessIdentity, "flag");
		if (!flagFile.Exists()) return null;
		var flag = new TextFile(flagFile.FullName);
		flag.Read();
		if (flag.Lines is not { Count: > 0 }) return null;
		return new TimestampedValue(flag.Lines[0]).Time;
	}
	public string Process
	{
		get
		{
			Read();
			return new TimestampedValue(Lines.Count == 0 ? "|" : Lines[0]).Value;
		}
		set
		{
			Lines = [new TimestampedValue(value).ToString()];
			Changed = true;
			Save();
		}
	}
	public new TimestampedValue Update(DateTimeOffset? time = null, string process = null)
	{
		var tv = new TimestampedValue(process ?? CurrentProcessId(), DateTimeOffset.UtcNow);
		if (time is not null) tv.Time = time.Value;
		Lines = [tv.ToString()];
		Changed = true;
		Save();
		return tv;
	}
	/// <summary>
	/// True when the activity flag is currently owned by this OS process.
	/// </summary>
	public bool IsOwnedByCurrentProcess
	{
		get
		{
			Read();
			if (Lines is not { Count: > 0 }) return false;
			return new TimestampedValue(Lines[0]).Value == CurrentProcessId();
		}
	}
	/// <summary>
	/// Expires this process activity window when, and only when, the flag content
	/// still identifies the current OS process. The file is retained as a diagnostic
	/// tombstone so cloud providers do not receive a delete/recreate cycle.
	/// </summary>
	public bool TryReleaseCurrentProcess()
	{
		if (!IsOwnedByCurrentProcess) return false;
		Update(
			time: DateTimeOffset.UnixEpoch,
			process: CurrentProcessId());
		return true;
	}
	/// <param name="dir">PitDir where all flag files live</param>
	/// <param name="subscriber">Application identity, e.g. "pits", "RAIkeep", "Nomsa".</param>
	public ProcessFlagFile(RaiPath dir, string subscriber = null)
		: base(dir, CurrentFlagName(subscriber))
	{
	}
}
