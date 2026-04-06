using System;
using System.Collections.Generic;
using System.Linq;
using OsLib;
namespace JsonPit;
/// <summary>
/// Flag file tracking which machine currently holds master rights for a pit.
/// Stored as a single-line text file: "MachineName|ISO8601-timestamp".
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
		lock (locker) { base.Save(backup); }
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
	/// True when this machine+process currently owns the master ticket and it hasn't expired.
	/// </summary>
	public bool IsOwnedByMe => !IsExpired && Originator == Environment.MachineName;
	/// <summary>
	/// Attempts to claim master rights for this machine.
	/// Succeeds when:
	///   - the ticket is expired (no active master), or
	///   - this machine already owns the ticket (renewal).
	/// On success, writes a fresh ticket valid for another <see cref="TicketDuration"/>.
	/// </summary>
	/// <returns>true if this machine now holds the master ticket</returns>
	public bool TryClaim()
	{
		lock (locker)
		{
			// Re-read from disk — another process may have claimed since our last read
			Read();
			if (!IsExpired && Originator != Environment.MachineName)
				return false;   // someone else has a valid ticket
			Update();           // writes MachineName|now
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
/// Per machine+application flag file.
/// Filename: "{MachineName}-{Subscriber}.flag", e.g. "Nkosikazi-pits.flag", "Mzansi-RAIkeep.flag".
/// Content: "{MachineName}:{ProcessName}:{PID}|{ISO8601-timestamp}" for diagnostics.
/// Both parts are needed: Subscriber alone isn't unique (runs on multiple servers),
/// MachineName alone isn't unique (multiple apps per machine).
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
	/// <param name="dir">PitDir where all flag files live</param>
	/// <param name="subscriber">Application identity, e.g. "pits", "RAIkeep", "Nomsa".</param>
	public ProcessFlagFile(RaiPath dir, string subscriber = null)
		: base(dir, FlagName(subscriber))
	{
	}
}
