using System;
using System.Collections.Generic;

namespace JsonPit;

/// <summary>Explicit options for one JsonPit maintenance pass (CR021).</summary>
public sealed record PitMaintenanceOptions
{
	/// <summary>Apply safe changes. False performs a read-only inventory.</summary>
	public bool Apply { get; init; }
	/// <summary>Also remove proven expired PID-specific process-window files.</summary>
	public bool PruneProcessFlags { get; init; }
	/// <summary>Required minimum age for process-window removal.</summary>
	public TimeSpan? OlderThan { get; init; }
	/// <summary>Also repair recognizable extensionless flags and recovery events.</summary>
	public bool RepairLegacyExtensions { get; init; }
}

/// <summary>Result of one explicit or operation-boundary CR021 maintenance pass.</summary>
public sealed class PitMaintenanceResult
{
	/// <summary>Creates a maintenance result for an existing or skipped pit target.</summary>
	public PitMaintenanceResult(string pitFile = "", bool applied = false)
	{
		PitFile = pitFile ?? string.Empty;
		Applied = applied;
	}

	public string PitFile { get; internal set; } = string.Empty;
	public bool Applied { get; internal set; }
	public bool CurrentMaster { get; internal set; }
	public bool CanonicalPersisted { get; internal set; }
	public int ChangeFilesObserved { get; internal set; }
	public int ChangeFilesValid { get; internal set; }
	public int ChangeFilesMerged { get; internal set; }
	public int ChangeFilesInvalid { get; internal set; }
	public int ChangeFilesEligible { get; internal set; }
	public int ChangeFilesRemoved { get; internal set; }
	public int ReceiptsObserved { get; internal set; }
	public int ReceiptsCreated { get; internal set; }
	public int ReceiptsRetained { get; internal set; }
	public int ReceiptsMalformed { get; internal set; }
	public int ReceiptsOrphaned { get; internal set; }
	public int ReceiptsRemoved { get; internal set; }
	public int ProcessFlagsActive { get; internal set; }
	public int ProcessFlagsExpired { get; internal set; }
	public int ProcessFlagsReleased { get; internal set; }
	public int ProcessFlagsNaturallyExpired { get; internal set; }
	public int ProcessFlagsMalformed { get; internal set; }
	public int ProcessFlagsPruned { get; internal set; }
	public int MasterFlagsObserved { get; internal set; }
	public int ConflictFlagsObserved { get; internal set; }
	public int LegacyArtifactsObserved { get; internal set; }
	public int LegacyArtifactsRepaired { get; internal set; }
	public List<string> Deferred { get; } = [];
	public List<string> Failures { get; } = [];
	public bool Succeeded => Failures.Count == 0;
}
