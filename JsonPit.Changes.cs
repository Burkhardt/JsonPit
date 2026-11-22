using Jil;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RaiUtils;
using OsLib;
// TODO: ChangeFile treatment s.#1318
namespace JsonPit;

public partial class Pit
{
	#region Change files
	/// <summary>
	/// Reads the canonical pit file into a private snapshot dictionary without
	/// constructing a second public <see cref="Pit"/> (CR003 §4: internal persistence
	/// comparison and merge mechanics use a private snapshot-reading mechanism).
	/// An absent new pit starts empty. An invalid previously observed snapshot aborts the write.
	/// </summary>
	private ConcurrentDictionary<string, PitItems> ReadCanonicalSnapshot()
	{
		var snapshot = new ConcurrentDictionary<string, PitItems>(Comparer);
		if (!JsonFile.Exists() && !canonicalSeen) return snapshot;
		var text = new TextFile(JsonFile.FullName).ReadAllText();
		ParseHistoricItems(PitJson.ParseArray(text), snapshot, DefaultMaxCount, markClean: true);
		return snapshot;
	}
	/// <summary>
	/// Find changes in memory vs disk and persist them as individual change files alongside the pit file.
	/// </summary>
	private int CreateChangeFiles()
	{
		FlushLiveItems();
		List<PitItem> dirty;
		stateGate.EnterWriteLock();
		try { dirty = HistoricItems.Values.SelectMany(history => history.Fragments).Where(item => !item.Valid()).ToList(); }
		finally { stateGate.ExitWriteLock(); }
		foreach (var fragment in dirty)
		{
			CreateChangeFile(fragment);
			fragment.Validate();
		}
		return dirty.Count;
	}
	/// <summary>
	/// Writes one fragment beside the pit file using
	/// {Modified.UtcTicks}_{ExactProcessIdentity}_{sha256-prefix4}.json.
	/// Identical content is idempotent; different content at the same path fails without overwriting.
	/// </summary>
	/// <returns>The change file that now durably carries this fragment.</returns>
	public RaiFile CreateChangeFile(PitItem item, string server = null)
	{
		if (item is null) return null;
		var (canonicalPayload, sha) = ChangeFile.CanonicalPayloadFor(item);
		var identity = server ?? ExactProcessIdentity;
		var fileName = ChangeFile.ComposeName(item.Modified, identity, sha[..4]);
		var changeFile = new RaiFile(PitDir, fileName, "json");
		if (changeFile.Exists())
		{
			if (new TextFile(changeFile.FullName).ReadAllText() == canonicalPayload)
				return changeFile; // idempotent republication
			throw new JsonPitPersistenceException(
				$"Change-file identity collision at '{changeFile.FullName}'; existing content differs.");
		}
		changeFile.mkdir();
		// Keep canonical UTF-8 bytes and no trailing line terminator for cross-engine parity.
		new TextFile(changeFile.FullName).WriteAllText(canonicalPayload);
		changeFile.AwaitMaterializing();
		return changeFile;
	}
	/// <summary>
	/// Grace period between a successful canonical persistence that accounts for a change
	/// file and that file's earliest permitted deletion (CR003: ten minutes measured from
	/// canonical persistence, not from original change-file creation). Settable for tests.
	/// </summary>
	public static TimeSpan ChangeFileCleanupGrace { get; set; } = TimeSpan.FromMinutes(10);
	/// <summary>
	/// MergeChanges — the CR003 protocol with CR021 durable cleanup receipts:
	/// 1. Read and hash/parse-validate all materialized change files; merge every valid file
	///    into the in-memory history (all participants — master and non-master).
	/// 2. (Exact master only) Persist the merged result as the new canonical pit file.
	/// 3. (Exact master only) Create an immutable ReceiptFile for each file accounted for by
	///    the canonical snapshot. An existing receipt is never refreshed across replay,
	///    restart, or master transfer.
	/// 4. (Exact master only) On this and later passes, revalidate current exact-master
	///    authority and exact canonical accounting, then delete files whose receipt grace elapsed.
	/// A file that fails hash or parse validation is not merged, not marked processed, and
	/// not deleted — it is reconsidered when it has materialized completely.
	/// </summary>
	public void MergeChanges() => Maintain(apply: true);

	/// <summary>
	/// Inspects or applies one explicit CR021 maintenance pass. Report-only mode performs
	/// no canonical, receipt, or deletion writes. Apply mode retains the existing
	/// MergeChanges behavior and adds restart-safe receipt-based cleanup.
	/// </summary>
	public PitMaintenanceResult Maintain(bool apply = false) =>
		Maintain(new PitMaintenanceOptions { Apply = apply });

	/// <summary>
	/// Inspects or applies CR021 maintenance, with separately authorized process-window
	/// pruning and legacy-extension repair.
	/// </summary>
	public PitMaintenanceResult Maintain(PitMaintenanceOptions options)
	{
		if (options is null) throw new ArgumentNullException(nameof(options));
		if (options.PruneProcessFlags && !options.Apply)
			throw new ArgumentException("Process-flag pruning requires Apply.", nameof(options));
		if (options.PruneProcessFlags && options.OlderThan is null)
			throw new ArgumentException("Process-flag pruning requires OlderThan.", nameof(options));
		if (!options.PruneProcessFlags && options.OlderThan is not null)
			throw new ArgumentException("OlderThan applies only to process-flag pruning.", nameof(options));
		if (options.OlderThan is { } olderThan && olderThan <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(options), "OlderThan must be positive.");
		if (options.RepairLegacyExtensions && !options.Apply)
			throw new ArgumentException("Legacy-extension repair requires Apply.", nameof(options));
		var result = new PitMaintenanceResult(JsonFile?.FullName, options.Apply);
		// CR022: PitDir is a creating property. Inspection and invalid-target handling
		// must check the non-creating canonical parent before any PitDir access.
		var directory = JsonFile?.Path;
		if (directory is null || !directory.Exists())
		{
			result.Deferred.Add($"Pit directory does not exist: {directory?.FullPath ?? string.Empty}");
			return result;
		}
		if (options.Apply) ApplyMaintenance(result);
		Monitor.Enter(_locker);
		try
		{
			if (!options.Apply) InspectMaintenanceUnderGate(result);
			InspectProcessFlags(result, options);
			InspectLegacyExtensions(result, options);
			if (options.ArchiveEvents)
				PitEventArchiver.Archive(directory, result, options.Apply);
			return result;
		}
		finally { Monitor.Exit(_locker); }
	}

	private void InspectProcessFlags(PitMaintenanceResult result, PitMaintenanceOptions options)
	{
		var now = DateTimeOffset.UtcNow;
		foreach (var file in PitDir.EnumerateFiles("*.flag").OrderBy(file => file.Name, StringComparer.Ordinal))
		{
			if (file.Name.Equals("Master", StringComparison.Ordinal))
			{
				result.MasterFlagsObserved++;
				continue;
			}
			if (file.Name.StartsWith("Master", StringComparison.Ordinal))
			{
				result.ConflictFlagsObserved++;
				continue;
			}
			try
			{
				var text = new TextFile(file.FullName);
				var lines = text.Read();
				if (lines.Count != 1)
				{
					result.ProcessFlagsMalformed++;
					result.Deferred.Add($"Process flag is malformed: {file.NameWithExtension}");
					continue;
				}
				var stamp = new TimestampedValue(lines[0]);
				var expired = string.IsNullOrWhiteSpace(stamp.Value) ||
					(now - stamp.Time) > MasterFlagFile.TicketDuration;
				if (!expired)
				{
					result.ProcessFlagsActive++;
					continue;
				}
				result.ProcessFlagsExpired++;
				if (stamp.Time == DateTimeOffset.UnixEpoch) result.ProcessFlagsReleased++;
				else result.ProcessFlagsNaturallyExpired++;
				if (!options.PruneProcessFlags || now - stamp.Time < options.OlderThan!.Value)
					continue;
				// Re-read immediately before deletion and prove it is still the same,
				// expired, PID-specific process window rather than authority/conflict data.
				var observed = lines[0];
				lines = text.Read();
				if (lines.Count != 1 || lines[0] != observed || !MasterFlagFile.IsExactProcessIdentity(file.Name))
				{
					result.Deferred.Add($"Process flag could not be proven PID-specific: {file.NameWithExtension}");
					continue;
				}
				stamp = new TimestampedValue(lines[0]);
				var pidSuffix = file.Name[(file.Name.LastIndexOf('-') + 1)..];
				if (!stamp.Value.EndsWith($":{pidSuffix}", StringComparison.Ordinal))
				{
					result.Deferred.Add($"Process flag filename/content PID mismatch: {file.NameWithExtension}");
					continue;
				}
				if (!string.IsNullOrWhiteSpace(stamp.Value) &&
					(now - stamp.Time) <= MasterFlagFile.TicketDuration)
					continue;
				text.rm();
				if (!text.Exists()) result.ProcessFlagsPruned++;
				else result.Failures.Add($"Expired process flag remained after removal: {text.FullName}");
			}
			catch (Exception ex)
			{
				result.ProcessFlagsMalformed++;
				result.Deferred.Add($"Process flag is not currently verifiable: {file.NameWithExtension}: {ex.Message}");
			}
		}
	}

	private void InspectLegacyExtensions(PitMaintenanceResult result, PitMaintenanceOptions options)
	{
		foreach (var file in PitDir.EnumerateFiles("*").OrderBy(file => file.NameWithExtension, StringComparer.Ordinal))
		{
			var leaf = file.NameWithExtension;
			if (leaf.EndsWith(".pit", StringComparison.OrdinalIgnoreCase) ||
				leaf.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
				leaf.EndsWith(".receipt", StringComparison.OrdinalIgnoreCase) ||
				leaf.EndsWith(".flag", StringComparison.OrdinalIgnoreCase))
				continue;
			if (!TryValidateLegacyProcessFlag(file, out var content)) continue;
			result.LegacyArtifactsObserved++;
			if (options.RepairLegacyExtensions)
				RepairLegacyTextFile(file, leaf, MasterFlagFileExtension, content, result);
		}

		var eventPath = PitDir / EventDirectory.Name;
		if (!eventPath.Exists()) return;
		foreach (var file in eventPath.EnumerateFiles("*").OrderBy(file => file.NameWithExtension, StringComparer.Ordinal))
		{
			var leaf = file.NameWithExtension;
			if (leaf.EndsWith($".{EventFile.Extension}", StringComparison.OrdinalIgnoreCase))
			{
				if (EventDirectory.TryExtractLegacyHash(file.Name, out var hash))
				{
					var cleanStem = file.Name[..file.Name.LastIndexOf('_')];
					if (TryValidateLegacyHashedEvent(file, hash, out var hashedContent))
					{
						result.LegacyArtifactsObserved++;
						if (options.RepairLegacyExtensions || options.Apply)
							RepairLegacyTextFile(file, cleanStem, EventFile.Extension, hashedContent, result);
					}
				}
				continue;
			}
			if (!TryValidateLegacyEvent(file, leaf, out var content)) continue;
			result.LegacyArtifactsObserved++;
			if (options.RepairLegacyExtensions || options.Apply)
				RepairLegacyTextFile(file, leaf, EventFile.Extension, content, result);
		}
	}

	private const string MasterFlagFileExtension = "flag";

	private static bool TryValidateLegacyHashedEvent(RaiFile file, string hash, out string content)
	{
		content = string.Empty;
		try
		{
			var text = new TextFile(file.FullName);
			content = text.ReadAllText();
			return CanonicalJson.Sha256Hex(content) == hash;
		}
		catch { return false; }
	}

	private static bool TryValidateLegacyProcessFlag(RaiFile file, out string content)
	{
		content = string.Empty;
		try
		{
			var text = new TextFile(file.FullName);
			var lines = text.Read();
			if (lines.Count != 1) return false;
			var stamp = new TimestampedValue(lines[0]);
			if (string.IsNullOrWhiteSpace(stamp.Value) || stamp.Time == DateTimeOffset.MinValue) return false;
			var lastDash = file.NameWithExtension.LastIndexOf('-');
			var pid = lastDash > 0 ? file.NameWithExtension[(lastDash + 1)..] : string.Empty;
			return lastDash > 0 && pid.Length > 0 && pid.All(char.IsAsciiDigit) &&
				stamp.Value.EndsWith($":{pid}", StringComparison.Ordinal) &&
				(content = text.ReadAllText()).Length > 0;
		}
		catch { return false; }
	}

	private static bool TryValidateLegacyEvent(RaiFile file, string leaf, out string content)
	{
		content = string.Empty;
		try
		{
			var text = new TextFile(file.FullName);
			content = text.ReadAllText();
			var parsed = PitJson.ParseObject(content);
			if ((string)parsed["SchemaVersion"] != RecoveryStatus.CurrentSchemaVersion ||
				parsed["EventId"] is null || parsed["UtcTime"] is null) return false;
			var separator = leaf.LastIndexOf('_');
			if (separator < 0) return false;
			var hash = leaf[(separator + 1)..];
			return hash.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
				CanonicalJson.Sha256Hex(content) == hash;
		}
		catch { return false; }
	}

	private static void RepairLegacyTextFile(
		RaiFile source,
		string logicalStem,
		string extension,
		string content,
		PitMaintenanceResult result)
	{
		try
		{
			var destination = new TextFile(source.Path, logicalStem, extension);
			destination.NameAndExt = (logicalStem, extension);
			if (destination.Exists())
			{
				if (destination.ReadAllText() != content)
				{
					result.Deferred.Add($"Legacy repair destination conflicts: {destination.FullName}");
					return;
				}
			}
			else
			{
				destination.cp(source);
				destination.AwaitMaterializing();
				if (!destination.Exists() || destination.ReadAllText() != content)
				{
					result.Failures.Add($"Legacy repair did not materialize correctly: {destination.FullName}");
					return;
				}
			}
			source.rm();
			if (!source.Exists()) result.LegacyArtifactsRepaired++;
			else result.Failures.Add($"Legacy source remained after repair: {source.FullName}");
		}
		catch (Exception ex)
		{
			result.Failures.Add($"Legacy repair failed for {source.NameWithExtension}: {ex.Message}");
		}
	}

	private PitMaintenanceResult InspectMaintenanceUnderGate(PitMaintenanceResult result)
	{
		var now = DateTimeOffset.UtcNow;
		result.CurrentMaster = IsCurrentExactMaster();
		foreach (var file in EnumerateChangeFiles().OrderBy(file => file.Name, StringComparer.Ordinal))
		{
			result.ChangeFilesObserved++;
			if (ChangeFile.ReadValidated(new RaiFile(file.FullName)) is null)
			{
				result.ChangeFilesInvalid++;
				continue;
			}
			result.ChangeFilesValid++;
			var receiptPath = ReceiptFile.PathFor(file.FullName);
			if (!receiptPath.Exists()) continue;
			result.ReceiptsObserved++;
			try
			{
				var receipt = new ReceiptFile(file.FullName);
				result.ReceiptsRetained++;
				if (now - receipt.Time >= ChangeFileCleanupGrace)
					result.ChangeFilesEligible++;
			}
			catch (Exception ex) when (ex is FormatException or System.IO.IOException)
			{
				result.ReceiptsMalformed++;
				result.Deferred.Add($"Receipt is not currently valid: {receiptPath.NameWithExtension}: {ex.Message}");
			}
		}
		InspectOrphanReceipts(result, now, apply: false);
		return result;
	}

	private void ApplyMaintenance(PitMaintenanceResult result)
	{
		if (!Invalid() && !EnumerateChangeFiles().Any() && !PitDir.EnumerateFiles("*.receipt").Any()) return;
		var turn = TryAcquireMaster();
		result.CurrentMaster = turn.Acquired;
		if (turn.Acquired)
			result.CanonicalPersisted = Store(turn.EarliestWriteUtc, claimTime, result).Outcome == PitSaveOutcome.Wrote;
		else
		{
			lock (_locker)
			{
				if (!ReadOnly) CreateChangeFiles();
				MergePending(result);
			}
			if (result.ChangeFilesMerged > 0) result.Deferred.Add("Cleanup requires current master authority.");
		}
	}

	private List<(RaiFile File, JArray Payload)> MergePending(PitMaintenanceResult result,
		ConcurrentDictionary<string, PitItems> candidate = null)
	{
		var mergedFiles = new List<(RaiFile File, JArray Payload)>();
		foreach (var file in EnumerateChangeFiles().OrderByDescending(file => file.Name, StringComparer.Ordinal))
		{
			result.ChangeFilesObserved++;
			try
			{
				var payload = ChangeFile.ReadValidated(new RaiFile(file.FullName));
				if (payload is null)
				{
					result.ChangeFilesInvalid++;
					continue; // not yet materialized/hash-valid — reconsidered on a later pass
				}
				result.ChangeFilesValid++;
				var changeSnapshot = new ConcurrentDictionary<string, PitItems>(Comparer);
				ParseHistoricItems(payload, changeSnapshot, DefaultMaxCount, markClean: true);
				foreach (var history in changeSnapshot.Values)
				{
					if (candidate is null) MergeIntoHistory(history);
					else
					{
						var merged = candidate.GetOrAdd(history.Key, key => PitItems.Create(key, DefaultMaxCount));
						foreach (var fragment in history.Fragments) merged = merged.Push(fragment);
						candidate[history.Key] = merged;
					}
				}
				result.ChangeFilesMerged++;
				mergedFiles.Add((new RaiFile(file.FullName), payload));
			}
			catch (Exception ex) when (ex is InvalidOperationException or JsonReaderException or JsonException or System.IO.IOException or FormatException)
			{
				result.ChangeFilesInvalid++;
				result.Deferred.Add($"Change file is not currently mergeable: {file.NameWithExtension}: {ex.Message}");
				Debug.WriteLine($"[JsonPit] MergeChanges skipped change file {file.Name}: {ex.Message}");
			}
		}
		return mergedFiles;
	}

	private void AccountAndClean(PitMaintenanceResult result, List<(RaiFile File, JArray Payload)> mergedFiles,
		ConcurrentDictionary<string, PitItems> canonicalSnapshot)
	{
		var now = DateTimeOffset.UtcNow;
		var publishedAt = JsonFile.LastWriteTimeUtc;
		var publishedLength = JsonFile.Length;
		var newlyEligible = 0;
		foreach (var entry in mergedFiles)
		{
			if (!CanonicalAccountsFor(entry.Payload, canonicalSnapshot))
			{
				result.Deferred.Add($"Canonical pit does not yet account for {entry.File.NameWithExtension}");
				continue;
			}
			var receiptPath = ReceiptFile.PathFor(entry.File.FullName);
			var existed = receiptPath.Exists();
			try
			{
				var receipt = new ReceiptFile(entry.File.FullName);
				result.ReceiptsObserved += existed ? 1 : 0;
				result.ReceiptsCreated += existed ? 0 : 1;
				result.ReceiptsRetained++;
				if (!existed) newlyEligible++;
				if (now - receipt.Time >= ChangeFileCleanupGrace)
					result.ChangeFilesEligible++;
			}
			catch (Exception ex) when (ex is FormatException or System.IO.IOException)
			{
				result.ReceiptsMalformed++;
				result.Deferred.Add($"Receipt is not currently valid: {receiptPath.NameWithExtension}: {ex.Message}");
			}
		}
		if (newlyEligible > 0)
			PublishRecoveryStatus(RecoveryStage.CleanupPending, RecoveryRole.Master, nameof(MergeChanges),
				$"{newlyEligible} merged change file(s) canonicalized; durable cleanup receipt grace of {ChangeFileCleanupGrace} started.",
				fileCount: newlyEligible);
		if (JsonFile.Exists() && (JsonFile.LastWriteTimeUtc != publishedAt || JsonFile.Length != publishedLength))
			canonicalSnapshot = ReadCanonicalSnapshot();
		CleanupEligibleChangeFiles(mergedFiles, canonicalSnapshot, now, result);
		InspectOrphanReceipts(result, now, apply: true);
	}

	/// <summary>
	/// Deletes change files whose immutable receipt grace has elapsed. Current-master-only;
	/// revalidates exact master authority and canonical accounting before deletion.
	/// </summary>
	private void CleanupEligibleChangeFiles(
		IEnumerable<(RaiFile File, JArray Payload)> mergedFiles,
		ConcurrentDictionary<string, PitItems> canonicalSnapshot,
		DateTimeOffset now,
		PitMaintenanceResult result)
	{
		foreach (var entry in mergedFiles)
		{
			if (!entry.File.Exists() || !ChangeFile.TryParseName(entry.File.Name, out _, out _, out _)) continue;
			var receiptPath = ReceiptFile.PathFor(entry.File.FullName);
			if (!receiptPath.Exists()) continue;
			try
			{
				var receipt = new ReceiptFile(entry.File.FullName);
				if (now - receipt.Time < ChangeFileCleanupGrace) continue;
				if (!IsCurrentExactMaster() || !JsonFile.Exists() ||
					!CanonicalAccountsFor(entry.Payload, canonicalSnapshot))
				{
					result.Deferred.Add($"Cleanup revalidation deferred {entry.File.NameWithExtension}");
					continue;
				}
				entry.File.rm(); // change first: failure retains the receipt
				if (entry.File.Exists())
				{
					result.Failures.Add($"Change file remained materialized after removal: {entry.File.FullName}");
					continue;
				}
				result.ChangeFilesRemoved++;
				receipt.rm();
				if (!receipt.Exists()) result.ReceiptsRemoved++;
				else result.Failures.Add($"Orphan receipt remained after change removal: {receipt.FullName}");
			}
			catch (Exception ex)
			{
				result.Failures.Add($"Cleanup failed for {entry.File.NameWithExtension}: {ex.Message}");
			}
		}
		if (result.ChangeFilesRemoved > 0)
			PublishRecoveryStatus(RecoveryStage.Completed, RecoveryRole.Master, nameof(MergeChanges),
				$"Retired {result.ChangeFilesRemoved} canonically accounted change file(s) and {result.ReceiptsRemoved} receipt(s).",
				fileCount: result.ChangeFilesRemoved);
	}

	private void InspectOrphanReceipts(PitMaintenanceResult result, DateTimeOffset now, bool apply)
	{
		foreach (var receiptPath in PitDir.EnumerateFiles("*.receipt").OrderBy(file => file.Name, StringComparer.Ordinal))
		{
			var changeFile = new RaiFile(PitDir, receiptPath.Name, "json");
			if (changeFile.Exists()) continue;
			result.ReceiptsObserved++;
			result.ReceiptsOrphaned++;
			try
			{
				// ReceiptFile accepts the deterministic missing change path and opens the
				// existing sibling receipt without recreating the change file.
				var receipt = new ReceiptFile(changeFile.FullName);
				if (!apply || !result.CurrentMaster || now - receipt.Time < ChangeFileCleanupGrace || !IsCurrentExactMaster())
					continue;
				receipt.rm();
				if (!receipt.Exists()) result.ReceiptsRemoved++;
				else result.Failures.Add($"Orphan receipt remained after removal: {receipt.FullName}");
			}
			catch (Exception ex) when (ex is FormatException or System.IO.IOException or ArgumentException)
			{
				result.ReceiptsMalformed++;
				result.Deferred.Add($"Orphan receipt is not currently valid: {receiptPath.NameWithExtension}: {ex.Message}");
			}
		}
	}

	private bool IsCurrentExactMaster()
	{
		if (unflagged) return true;
		return OwnsClaim();
	}

	private bool CanonicalAccountsFor(
		JArray payload,
		ConcurrentDictionary<string, PitItems> canonicalSnapshot)
	{
		if (!JsonFile.Exists()) return false;
		var changeSnapshot = new ConcurrentDictionary<string, PitItems>(Comparer);
		ParseHistoricItems(payload, changeSnapshot, DefaultMaxCount, markClean: true);
		foreach (var changeEntry in changeSnapshot)
		{
			if (!canonicalSnapshot.TryGetValue(changeEntry.Key, out var canonicalHistory))
				return false;
			var merged = canonicalHistory;
			foreach (var fragment in changeEntry.Value.Fragments)
				merged = merged.Push(fragment);
			if (!HistoriesEqual(canonicalHistory, merged)) return false;
		}
		return true;
	}

	private static bool HistoriesEqual(PitItems left, PitItems right)
	{
		if (left.Fragments.Count != right.Fragments.Count) return false;
		for (var i = 0; i < left.Fragments.Count; i++)
			if (!JToken.DeepEquals(left.Fragments[i], right.Fragments[i])) return false;
		return true;
	}
	public void MergeIntoHistory(PitItems changeItems)
	{
		WithLiveState(changeItems.Key, slot =>
		{
			FlushFallback(slot);
			while (true)
			{
				var currentStore = HistoricItems.GetOrAdd(changeItems.Key, key => PitItems.Create(key, DefaultMaxCount));
				var newStore = currentStore;
				foreach (var item in changeItems)
					newStore = newStore.Push(item);
				if (ReferenceEquals(newStore, currentStore))
					return false; // exact replay duplicates are harmless and ignored
				if (HistoricItems.TryUpdate(changeItems.Key, newStore, currentStore))
				{
					PublishLiveState(slot, changeItems.Key, newStore);
					return true;
				}
			}
		});
	}
	#endregion
}
