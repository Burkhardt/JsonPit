using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using OsLib;

namespace JsonPit;

/// <summary>
/// Structured recovery stages (CR003, coordinated v3.13.2).
/// </summary>
public enum RecoveryStage
{
	ConflictDetected,
	RoleDetermined,
	ChangeFilesPublished,
	Canonicalized,
	CleanupPending,
	Completed,
	DeferredForRetry,
	Failed
}

/// <summary>
/// Local recovery role of this exact process for one evaluation.
/// </summary>
public enum RecoveryRole
{
	None,
	Master,
	Loser,
	Observer
}

/// <summary>
/// Immutable structured recovery status (CR003, coordinated v3.13.2). Exposed live via
/// <see cref="Pit.LastRecoveryStatus"/> / <see cref="Pit.RecoveryStatusChanged"/> and
/// written durably as one canonical-JSON <c>.event</c> file per stage occurrence under
/// the owning pit root's <c>Events</c> child directory.
/// </summary>
public sealed record RecoveryStatus(
	string SchemaVersion,
	Guid EventId,
	DateTimeOffset UtcTime,
	LogLevel Level,
	RecoveryStage Stage,
	string Pit,
	string Machine,
	string Process,
	string Master,
	RecoveryRole Role,
	int FragmentCount,
	int FileCount,
	Guid CorrelationId,
	string Operation,
	string Message,
	string ExceptionDetail)
{
	/// <summary>Current schema version of the durable JsonPit audit event content.</summary>
	public const string CurrentSchemaVersion = "1";

	/// <summary>
	/// Default severity for each recovery stage. <see cref="LogLevel.None"/> is never
	/// written as an event.
	/// </summary>
	public static LogLevel DefaultLevel(RecoveryStage stage) => stage switch
	{
		RecoveryStage.ConflictDetected => LogLevel.Warning,
		RecoveryStage.RoleDetermined => LogLevel.Information,
		RecoveryStage.ChangeFilesPublished => LogLevel.Information,
		RecoveryStage.Canonicalized => LogLevel.Information,
		RecoveryStage.CleanupPending => LogLevel.Debug,
		RecoveryStage.Completed => LogLevel.Information,
		RecoveryStage.DeferredForRetry => LogLevel.Warning,
		RecoveryStage.Failed => LogLevel.Error,
		_ => LogLevel.Information
	};

	/// <summary>Serializes this status to the durable JsonPit audit event schema.</summary>
	public JObject ToJObject() => new()
	{
		["SchemaVersion"] = SchemaVersion,
		["EventId"] = EventId.ToString("D"),
		["UtcTime"] = UtcTime.UtcDateTime.ToString("o"),
		["UtcTicks"] = UtcTime.UtcTicks,
		["Level"] = Level.ToString(),
		["Stage"] = Stage.ToString(),
		["Pit"] = Pit,
		["Machine"] = Machine,
		["Process"] = Process,
		["Master"] = Master ?? string.Empty,
		["Role"] = Role.ToString(),
		["FragmentCount"] = FragmentCount,
		["FileCount"] = FileCount,
		["CorrelationId"] = CorrelationId.ToString("D"),
		["Operation"] = Operation ?? string.Empty,
		["Message"] = Message ?? string.Empty,
		["Exception"] = ExceptionDetail ?? string.Empty
	};
}

/// <summary>
/// One parsed durable JsonPit audit event as read back from a pit's <c>Events</c>
/// directory. JsonPit — not OsLib — owns this schema and its interpretation.
/// </summary>
public sealed class PitAuditEvent
{
	public string FileName { get; }
	public JObject Content { get; }
	public string Machine { get; }
	public DateTimeOffset UtcTime { get; }
	public LogLevel Level { get; }
	public string Stage { get; }
	public string EventId { get; }
	public string Message { get; }

	public PitAuditEvent(string fileName, JObject content)
	{
		FileName = fileName;
		Content = content;
		Machine = (string)content["Machine"] ?? string.Empty;
		var ticks = (long?)content["UtcTicks"];
		UtcTime = ticks is not null
			? new DateTimeOffset(ticks.Value, TimeSpan.Zero)
			: DateTimeOffset.TryParse((string)content["UtcTime"], out var parsed) ? parsed.ToUniversalTime() : DateTimeOffset.MinValue;
		Level = Enum.TryParse<LogLevel>((string)content["Level"], ignoreCase: true, out var level) ? level : LogLevel.Trace;
		Stage = (string)content["Stage"] ?? string.Empty;
		EventId = (string)content["EventId"] ?? string.Empty;
		Message = (string)content["Message"] ?? string.Empty;
	}
}

/// <summary>
/// Read-only audit access to a pit's durable events (CR003, coordinated v3.13.2).
/// Reading never opens a <see cref="Pit"/>, creates a process or master flag, acquires
/// master authority, or writes an audit event.
/// </summary>
public static class PitAudit
{
	/// <summary>
	/// Reads and filters the durable audit events of the pit rooted at
	/// <paramref name="pitDirectory"/> (the directory containing the canonical
	/// <c>.pit</c> file and its <c>Events</c> child).
	/// </summary>
	/// <param name="pitDirectory">The pit's own directory (pit root / pit name).</param>
	/// <param name="machineFilter"><c>all</c>, <c>local</c>, or a machine name; case-insensitive.</param>
	/// <param name="minLevel">Inclusive minimum severity; defaults to <see cref="LogLevel.Trace"/>.</param>
	/// <returns>Events ordered deterministically by machine, UTC time, and event identity.</returns>
	public static IReadOnlyList<PitAuditEvent> Read(RaiPath pitDirectory, string machineFilter = "all", LogLevel minLevel = LogLevel.Trace)
	{
		if (pitDirectory is null) throw new ArgumentNullException(nameof(pitDirectory));
		var events = EventDirectory.Events(pitDirectory)
			.Select(kvp => new PitAuditEvent(kvp.Key, kvp.Value));
		var filter = string.IsNullOrWhiteSpace(machineFilter) ? "all" : machineFilter.Trim();
		if (!filter.Equals("all", StringComparison.OrdinalIgnoreCase))
		{
			var machine = filter.Equals("local", StringComparison.OrdinalIgnoreCase)
				? Environment.MachineName
				: filter;
			events = events.Where(e => string.Equals(e.Machine, machine, StringComparison.OrdinalIgnoreCase));
		}
		if (minLevel > LogLevel.Trace)
			events = events.Where(e => e.Level >= minLevel && e.Level != LogLevel.None);
		return events
			.OrderBy(e => e.Machine, StringComparer.Ordinal)
			.ThenBy(e => e.UtcTime)
			.ThenBy(e => e.EventId, StringComparer.Ordinal)
			.ToList();
	}

	/// <summary>
	/// Parses a case-insensitive <see cref="LogLevel"/> name for the inclusive
	/// minimum-severity audit filter. <c>None</c> is not a valid filter value.
	/// </summary>
	public static bool TryParseLevel(string text, out LogLevel level)
	{
		if (Enum.TryParse(text, ignoreCase: true, out level) && level != LogLevel.None)
			return true;
		level = LogLevel.Trace;
		return false;
	}
}
