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
	#region Recovery (CR003 + JsonPit-CONCEPT-Live-Split-Master-Recovery)
	/// <summary>Debounce/materialization delay between a watcher signal and its queued recovery evaluation. Settable for tests.</summary>
	public static TimeSpan RecoveryDebounce { get; set; } = TimeSpan.FromSeconds(1);
	/// <summary>Extra safety grace after a named claimant's process-window expiry before the exact current master may retire an orphaned longer conflict flag. Settable for tests.</summary>
	public static TimeSpan OrphanedConflictFlagGrace { get; set; } = TimeSpan.FromMinutes(10);
	/// <summary>
	/// In-memory recovery write set for the current exact-process master tenure
	/// (concept §5). Keyed internally by clean change-file identity plus canonical-content
	/// hash, which deduplicates exact replays without lengthening public filenames. Entries are
	/// removed per fragment once an ordinary change file is locally written, materialized,
	/// and parsed (with hash verification for legacy names) — no cloud or master acknowledgement is required.
	/// </summary>
	private readonly ConcurrentDictionary<string, PitItem> recoveryWriteSet = new(StringComparer.Ordinal);
	private bool hasMasterTenure;
	private Guid tenureCorrelationId;
	private System.IO.FileSystemWatcher conflictWatcher;
	private int recoveryEvaluationQueued;
	private readonly CancellationTokenSource recoveryCts = new();
	/// <summary>Immutable structured status of the most recent recovery-related activity of this live pit.</summary>
	public RecoveryStatus LastRecoveryStatus { get; private set; }
	/// <summary>
	/// Optional live notification of recovery status changes. Applications do not need to
	/// subscribe or respond for recovery correctness.
	/// </summary>
	public event Action<RecoveryStatus> RecoveryStatusChanged;
	private void RecordInRecoveryWriteSet(PitItem fragment)
	{
		if (fragment is null) return;
		recoveryWriteSet.TryAdd(RecoveryWriteSetKey(fragment), fragment);
	}
	private string RecoveryWriteSetKey(PitItem fragment)
	{
		var (_, sha) = ChangeFile.CanonicalPayloadFor(fragment);
		return $"{ChangeFile.ComposeName(fragment, ExactProcessIdentity)}_{sha}";
	}
	/// <summary>
	/// Tracks exact-process master tenures (concept §5): acquisition after not owning
	/// starts a tenure; renewal by the same exact process continues it; authority observed
	/// at another exact process ends it and triggers the durable live-handoff export.
	/// Mere lease expiry without a new owner is not a transfer and exports nothing.
	/// </summary>
	protected override void OnMasterAuthorityEvaluated(bool acquired, string recordedOwner, bool ownerLeaseValid)
	{
		if (unflagged) return;
		if (acquired)
		{
			if (!hasMasterTenure)
			{
				hasMasterTenure = true;
				tenureCorrelationId = Guid.NewGuid();
			}
			return;
		}
		if (hasMasterTenure && ownerLeaseValid && recordedOwner != ExactProcessIdentity)
		{
			hasMasterTenure = false;
			ExportRecoveryUnion("LiveAuthorityTransfer");
		}
	}
	/// <summary>
	/// Captures the union of the tenure recovery write set and all currently dirty live
	/// fragments through the brief exclusive snapshot barrier, deduplicated by
	/// internal fragment identity.
	/// </summary>
	private Dictionary<string, PitItem> SnapshotRecoveryUnion()
	{
		var union = new Dictionary<string, PitItem>(StringComparer.Ordinal);
		stateGate.EnterWriteLock();
		try
		{
			FlushLiveItems();
			foreach (var entry in recoveryWriteSet)
				union[entry.Key] = entry.Value;
			foreach (var kvp in HistoricItems)
				foreach (var fragment in kvp.Value.Fragments)
					if (!fragment.Valid())
						union[RecoveryWriteSetKey(fragment)] = fragment;
		}
		finally { stateGate.ExitWriteLock(); }
		return union;
	}
	/// <summary>
	/// Publishes fragments as ordinary clean change files and transfers recovery
	/// responsibility per fragment: an entry leaves the write set only after its file is
	/// locally written, materialized, and parsed. Failed fragments retain
	/// their entries for idempotent retry; successful siblings are released independently.
	/// </summary>
	/// <returns>(published, failed) fragment counts.</returns>
	private (int Published, int Failed) PublishFragmentsAsChangeFiles(IReadOnlyDictionary<string, PitItem> fragments, string operation, Guid correlationId)
	{
		int published = 0, failed = 0;
		foreach (var entry in fragments)
		{
			try
			{
				var file = CreateChangeFile(entry.Value);
				if (file is not null && ChangeFile.ReadValidated(file) is not null)
				{
					recoveryWriteSet.TryRemove(entry.Key, out _);
					published++;
				}
				else failed++;
			}
			catch (Exception ex)
			{
				failed++;
				Debug.WriteLine($"[JsonPit] change-file publication failed for {entry.Key}: {ex.Message}");
			}
		}
		if (published > 0)
			PublishRecoveryStatus(RecoveryStage.ChangeFilesPublished, CurrentRecoveryRole(), operation,
				$"{published} fragment(s) published as validated ordinary change files.",
				fragmentCount: published, fileCount: published, correlationId: correlationId);
		return (published, failed);
	}
	/// <summary>
	/// Durable live-handoff export (concept §5): publish the completed tenure's write set
	/// plus currently dirty fragments as ordinary change files and continue as a non-master.
	/// </summary>
	private int ExportRecoveryUnion(string operation)
	{
		var union = SnapshotRecoveryUnion();
		if (union.Count == 0) return 0;
		var correlation = tenureCorrelationId == Guid.Empty ? Guid.NewGuid() : tenureCorrelationId;
		var (published, failed) = PublishFragmentsAsChangeFiles(union, operation, correlation);
		if (failed > 0)
			PublishRecoveryStatus(RecoveryStage.DeferredForRetry, CurrentRecoveryRole(), operation,
				$"{failed} fragment(s) could not be published yet and remain in the recovery write set for retry.",
				fragmentCount: failed, correlationId: correlation);
		else if (published > 0)
			PublishRecoveryStatus(RecoveryStage.Completed, CurrentRecoveryRole(), operation,
				"Completed tenure write set and dirty fragments exported as ordinary change files.",
				fragmentCount: published, fileCount: published, correlationId: correlation);
		return published;
	}
	private RecoveryRole CurrentRecoveryRole()
	{
		if (unflagged) return RecoveryRole.None;
		try
		{
			return OwnsClaim() ? RecoveryRole.Master : RecoveryRole.Observer;
		}
		catch { return RecoveryRole.None; }
	}
	/// <summary>
	/// Updates the immutable live status first, then notifies optional observers, then
	/// writes the durable canonical-JSON audit event (flagged pits only). CR-related
	/// diagnostics use these structured events instead of Debug.WriteLine.
	/// </summary>
	private void PublishRecoveryStatus(RecoveryStage stage, RecoveryRole role, string operation, string message,
		int fragmentCount = 0, int fileCount = 0, Exception exception = null, Guid? correlationId = null,
		Microsoft.Extensions.Logging.LogLevel? level = null)
	{
		var status = new RecoveryStatus(
			RecoveryStatus.CurrentSchemaVersion,
			Guid.NewGuid(),
			DateTimeOffset.UtcNow,
			level ?? RecoveryStatus.DefaultLevel(stage),
			stage,
			JsonFile?.FullName ?? string.Empty,
			Environment.MachineName,
			ExactProcessIdentity,
			unflagged ? string.Empty : SafeCurrentMaster(),
			role,
			fragmentCount,
			fileCount,
			correlationId ?? tenureCorrelationId,
			operation,
			message,
			exception?.ToString());
		LastRecoveryStatus = status;
		try { RecoveryStatusChanged?.Invoke(status); }
		catch (Exception ex) { Debug.WriteLine($"[JsonPit] RecoveryStatusChanged observer threw: {ex.Message}"); }
		if (!unflagged && status.Level is >= Microsoft.Extensions.Logging.LogLevel.Warning and <= Microsoft.Extensions.Logging.LogLevel.Critical)
			TryWriteDurableEvent(status);
	}
	private string SafeCurrentMaster()
	{
		try { return MasterFlag().Originator ?? string.Empty; }
		catch { return string.Empty; }
	}
	/// <summary>
	/// Writes one durable audit event; ordinary event-recording failure must never
	/// interrupt the operation being audited.
	/// </summary>
	/// <returns>The validated event file, or null when it could not be produced.</returns>
	private EventFile TryWriteDurableEvent(RecoveryStatus status)
	{
		try
		{
			var stem = $"{status.UtcTime.UtcTicks}_{ExactProcessIdentity}_{status.Stage}";
			return new EventFile(PitDir, stem, status.ToJObject());
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[JsonPit] durable event write failed: {ex.Message}");
			return null;
		}
	}
	/// <summary>
	/// Starts the native filesystem watcher for provider-created <c>Master*.flag</c>
	/// signals. Only live writable flagged pits watch; callbacks are lightweight signals
	/// that queue at most one debounced recovery evaluation and never perform persistence
	/// on the callback thread. Watcher errors trigger an operation-boundary style rescan.
	/// </summary>
	private void StartConflictWatcher()
	{
		if (ReadOnly || unflagged || conflictWatcher is not null) return;
		try
		{
			var directory = PitDir; // creates the pit directory if needed
			// FileSystemWatcher remains rooted while native monitoring is active. Its event
			// delegates therefore must not close over this Pit, or an abandoned Pit can never
			// become finalizable and its weak path-registry entry continues to look live.
			var weakOwner = new WeakReference<Pit>(this);
			conflictWatcher = new System.IO.FileSystemWatcher(directory.FullPath, "Master*.flag")
			{
				NotifyFilter = System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.Size,
				IncludeSubdirectories = false
			};
			conflictWatcher.Created += (_, _) => QueueRecoveryEvaluation(weakOwner, "Watcher");
			conflictWatcher.Changed += (_, _) => QueueRecoveryEvaluation(weakOwner, "Watcher");
			conflictWatcher.Renamed += (_, _) => QueueRecoveryEvaluation(weakOwner, "Watcher");
			conflictWatcher.Error += (_, _) => QueueRecoveryEvaluation(weakOwner, "WatcherError");
			conflictWatcher.EnableRaisingEvents = true;
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[JsonPit] conflict watcher unavailable for {JsonFile.Name}: {ex.Message}");
			conflictWatcher = null; // boundary scans remain the correctness mechanism
		}
	}
	private void StopConflictWatcher()
	{
		try { recoveryCts.Cancel(); } catch { }
		var watcher = conflictWatcher;
		conflictWatcher = null;
		if (watcher is not null)
		{
			try
			{
				watcher.EnableRaisingEvents = false;
				watcher.Dispose();
			}
			catch { }
		}
	}
	/// <summary>
	/// Debounces duplicate or partially materialized notifications and queues at most one
	/// recovery evaluation for this pit.
	/// </summary>
	private static void QueueRecoveryEvaluation(WeakReference<Pit> weakOwner, string operation)
	{
		if (weakOwner.TryGetTarget(out var owner))
			owner.QueueRecoveryEvaluation(operation);
	}

	private void QueueRecoveryEvaluation(string operation)
	{
		if (disposed || ReadOnly || unflagged) return;
		if (Interlocked.CompareExchange(ref recoveryEvaluationQueued, 1, 0) != 0) return;
		var token = recoveryCts.Token;
		_ = RunQueuedRecoveryEvaluation(new WeakReference<Pit>(this), token, operation);
	}

	private static async Task RunQueuedRecoveryEvaluation(
		WeakReference<Pit> weakOwner, CancellationToken token, string operation)
	{
		Pit owner = null;
		try
		{
			await Task.Delay(RecoveryDebounce, token).ConfigureAwait(false); // wait for materialization
			if (!weakOwner.TryGetTarget(out owner) || token.IsCancellationRequested || owner.disposed) return;
			owner.EvaluateRecovery(operation);
		}
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			// Never let a recovery failure escape on a background/callback path.
			if ((owner is not null || weakOwner.TryGetTarget(out owner)) && !owner.disposed)
				owner.PublishRecoveryStatus(RecoveryStage.DeferredForRetry, owner.CurrentRecoveryRole(), operation,
					$"Queued recovery evaluation failed and is deferred to the next operation boundary: {ex.Message}",
					exception: ex);
		}
		finally
		{
			if (owner is not null || weakOwner.TryGetTarget(out owner))
				Interlocked.Exchange(ref owner.recoveryEvaluationQueued, 0);
		}
	}
	/// <summary>
	/// Operation-boundary conflict scan (construction, master acquisition, Save, Reload,
	/// watcher-error recovery). Notifications are signals, not guaranteed delivery; these
	/// scans are the correctness mechanism and involve no polling loop.
	/// </summary>
	private void ScanForConflictSignals(string operation)
	{
		if (disposed || ReadOnly || unflagged) return;
		try
		{
			if (MasterFlagFile.ConflictFlags(PitDir).Any())
				EvaluateRecovery(operation);
		}
		catch (Exception ex)
		{
			PublishRecoveryStatus(RecoveryStage.DeferredForRetry, CurrentRecoveryRole(), operation,
				$"Conflict scan failed and is deferred to the next operation boundary: {ex.Message}", exception: ex);
		}
	}
	/// <summary>
	/// One recovery evaluation (concept §4/§6/§7): rescans the directory, reads exact
	/// canonical <c>Master.flag</c>, determines this process's role, and performs the
	/// complementary recovery actions through the persistence/recovery gate. Only one
	/// evaluation runs for a pit at a time.
	/// </summary>
	private void EvaluateRecovery(string operation)
	{
		Monitor.Enter(_locker); // persistence/recovery gate
		try
		{
			if (disposed) return;
			var conflictFlags = MasterFlagFile.ConflictFlags(PitDir).ToList();
			if (conflictFlags.Count == 0) return;
			var correlation = Guid.NewGuid();
			var canonicalOwner = SafeCurrentMaster();
			PublishRecoveryStatus(RecoveryStage.ConflictDetected, RecoveryRole.None, operation,
				$"{conflictFlags.Count} longer Master*.flag conflict signal(s) present: " +
				string.Join(", ", conflictFlags.Select(f => f.NameWithExtension)),
				fileCount: conflictFlags.Count, correlationId: correlation);
			// The longer flags naming this exact process identify it as a losing claimant.
			var myConflictFlags = conflictFlags
				.Where(f => ReadFlagOwner(f) == ExactProcessIdentity)
				.ToList();
			var role = OwnsClaim()
				? RecoveryRole.Master
				: myConflictFlags.Count > 0 ? RecoveryRole.Loser : RecoveryRole.Observer;
			PublishRecoveryStatus(RecoveryStage.RoleDetermined, role, operation,
				$"Canonical Master.flag names '{canonicalOwner}'; local role is {role}.",
				correlationId: correlation);
			switch (role)
			{
				case RecoveryRole.Loser:
					RecoverAsLosingClaimant(myConflictFlags, operation, correlation);
					break;
				case RecoveryRole.Master:
					RetireOrphanedConflictFlags(conflictFlags, operation, correlation);
					break;
				default:
					// Observers merge published change files through the ordinary paths.
					break;
			}
		}
		finally { Monitor.Exit(_locker); }
	}
	private static string ReadFlagOwner(RaiFile flagFile)
	{
		try
		{
			var flag = new TextFile(flagFile.FullName);
			flag.Read();
			if (flag.Lines is not { Count: > 0 }) return string.Empty;
			return MasterTicket.Parse(flag.Lines[0]).Owner;
		}
		catch { return string.Empty; }
	}
	/// <summary>
	/// Losing-claimant recovery (concept §7): stop canonical writes, publish the union of
	/// the tenure write set and dirty fragments as ordinary small change files, and delete
	/// only this process's longer conflict flag after every required recovery fragment has
	/// a locally materialized, hash-valid ordinary change file. Exact canonical
	/// <c>Master.flag</c> and canonical <c>Object*.pit</c> files are never touched here.
	/// </summary>
	private void RecoverAsLosingClaimant(List<RaiFile> myConflictFlags, string operation, Guid correlation)
	{
		hasMasterTenure = false; // canonical-write authority stops immediately
		var union = SnapshotRecoveryUnion();
		var (published, failed) = PublishFragmentsAsChangeFiles(union, operation, correlation);
		if (failed > 0)
		{
			PublishRecoveryStatus(RecoveryStage.DeferredForRetry, RecoveryRole.Loser, operation,
				$"{failed} recovery fragment(s) not yet durable; the longer conflict flag is retained as evidence.",
				fragmentCount: failed, correlationId: correlation);
			return;
		}
		// Deletion of the longer losing claim is the loser's completion signal.
		foreach (var flag in myConflictFlags)
		{
			try { flag.rm(); }
			catch (Exception ex)
			{
				PublishRecoveryStatus(RecoveryStage.DeferredForRetry, RecoveryRole.Loser, operation,
					$"Recovery fragments are durable but conflict flag '{flag.NameWithExtension}' could not be retired yet: {ex.Message}",
					exception: ex, correlationId: correlation);
				return;
			}
		}
		PublishRecoveryStatus(RecoveryStage.Completed, RecoveryRole.Loser, operation,
			$"Losing tenure recovered: {published} fragment(s) durable as ordinary change files; longer conflict flag retired.",
			fragmentCount: published, fileCount: published, correlationId: correlation);
	}
	/// <summary>
	/// Orphaned-signal retirement (concept §7): only the exact current master retires a
	/// longer conflict flag whose named process has no active window, and only after
	/// window expiry plus <see cref="OrphanedConflictFlagGrace"/>, with a locally
	/// validated <c>Critical</c> evidence event written first. A still-live loser's
	/// conflict evidence is never retired on its behalf, and no noncanonical
	/// <c>Object*.pit</c> is inspected or merged.
	/// </summary>
	private void RetireOrphanedConflictFlags(List<RaiFile> conflictFlags, string operation, Guid correlation)
	{
		foreach (var flag in conflictFlags)
		{
			var claimant = ReadFlagOwner(flag);
			if (string.IsNullOrEmpty(claimant) || claimant == ExactProcessIdentity)
				continue;
			if (ProcessFlagFile.IsProcessWindowActive(PitDir, claimant))
				continue; // still-live loser retires its own evidence
			var windowTime = ProcessFlagFile.ProcessWindowTime(PitDir, claimant);
			DateTimeOffset flagTime;
			try { flagTime = new MasterFlagFile(flag.Path, flag.Name).Time; }
			catch { flagTime = DateTimeOffset.MinValue; }
			var referenceTime = windowTime ?? flagTime;
			var eligibleAt = referenceTime + MasterFlagFile.TicketDuration + OrphanedConflictFlagGrace;
			if (DateTimeOffset.UtcNow < eligibleAt)
			{
				PublishRecoveryStatus(RecoveryStage.CleanupPending, RecoveryRole.Master, operation,
					$"Orphaned conflict flag '{flag.NameWithExtension}' (claimant '{claimant}') awaits expiry plus grace until {eligibleAt:o}.",
					fileCount: 1, correlationId: correlation);
				continue;
			}
			// Written and locally validated Critical evidence is a prerequisite to deletion.
			string flagContent;
			try { flagContent = string.Join(Environment.NewLine, new TextFile(flag.FullName).Read()); }
			catch { flagContent = string.Empty; }
			var evidence = new RecoveryStatus(
				RecoveryStatus.CurrentSchemaVersion, Guid.NewGuid(), DateTimeOffset.UtcNow,
				Microsoft.Extensions.Logging.LogLevel.Critical, RecoveryStage.ConflictDetected,
				JsonFile.FullName, Environment.MachineName, ExactProcessIdentity, SafeCurrentMaster(),
				RecoveryRole.Master, 0, 1, correlation, operation,
				$"Orphaned master-conflict signal retired: file '{flag.NameWithExtension}', content '{flagContent}', " +
				$"claimant '{claimant}', claimant window time '{windowTime?.ToString("o") ?? "none"}', flag time '{flagTime:o}'. " +
				"No live recovery write set was available for the named process; its unexported in-memory " +
				"fragments are outside the live recovery guarantee.",
				null);
			LastRecoveryStatus = evidence;
			try { RecoveryStatusChanged?.Invoke(evidence); } catch { }
			var eventFile = TryWriteDurableEvent(evidence);
			if (eventFile is null || EventDirectory.Events(PitDir).ContainsKey(eventFile.NameWithExtension) == false)
			{
				PublishRecoveryStatus(RecoveryStage.DeferredForRetry, RecoveryRole.Master, operation,
					$"Critical evidence for orphaned flag '{flag.NameWithExtension}' could not be validated; deletion deferred.",
					fileCount: 1, correlationId: correlation);
				continue;
			}
			try
			{
				flag.rm(); // only the longer flag — exact Master.flag is never deleted or altered
				PublishRecoveryStatus(RecoveryStage.Completed, RecoveryRole.Master, operation,
					$"Orphaned conflict flag '{flag.NameWithExtension}' retired after validated Critical evidence.",
					fileCount: 1, correlationId: correlation);
			}
			catch (Exception ex)
			{
				PublishRecoveryStatus(RecoveryStage.DeferredForRetry, RecoveryRole.Master, operation,
					$"Orphaned conflict flag '{flag.NameWithExtension}' could not be deleted yet: {ex.Message}",
					exception: ex, correlationId: correlation);
			}
		}
	}
	#endregion
	#region IDisposable
	public void Dispose()
	{
		Dispose(true);
		GC.SuppressFinalize(this);
	}
	/// <summary>
	/// Explicit disposal of a writable pit is a durability boundary (CR003 / concept §8):
	/// under the persistence/recovery gate it publishes the tenure recovery write set plus
	/// currently dirty fragments as ordinary clean change files, optionally
	/// completes a canonical save while it still owns exact master authority, and only
	/// then releases process authority, the watcher, and the path registration. Failure to
	/// make accepted fragments durable during explicit shutdown is Critical and throws a
	/// descriptive persistence exception. Finalizers perform no filesystem I/O; crash,
	/// forced termination, and power loss remain outside the live recovery guarantee.
	/// </summary>
	protected virtual void Dispose(bool disposing)
	{
		if (disposed) return;
		if (!disposing)
		{
			// Finalizer: no filesystem, cloud-root, flag, event, save, or recovery I/O.
			disposed = true;
			return;
		}
		try
		{
			if (!ReadOnly)
			{
				Monitor.Enter(_locker); // shutdown cannot overlap Save or recovery work
				try
				{
					if (unflagged)
					{
						// Unflagged pits have no flag/change-file protocol: persist directly.
						if (Invalid()) Store(DateTimeOffset.UtcNow, claimTime);
					}
					else
					{
						try
						{
							if (Invalid()) Save();
						}
						catch (Exception ex)
						{
							PublishRecoveryStatus(RecoveryStage.DeferredForRetry, CurrentRecoveryRole(), nameof(Dispose),
								$"Shutdown save failed. Publishing outstanding fragments: {ex.Message}", exception: ex);
						}
						var union = SnapshotRecoveryUnion();
						var (published, failed) = PublishFragmentsAsChangeFiles(union, nameof(Dispose), tenureCorrelationId);
						if (failed > 0)
						{
							PublishRecoveryStatus(RecoveryStage.Failed, CurrentRecoveryRole(), nameof(Dispose),
								$"Explicit shutdown could not make {failed} accepted fragment(s) durable.",
								fragmentCount: failed, level: Microsoft.Extensions.Logging.LogLevel.Critical);
							throw new JsonPitPersistenceException(
								$"Graceful disposal of pit '{JsonFile.FullName}' could not make {failed} accepted fragment(s) " +
								$"durable as ordinary change files ({published} succeeded). The failed fragments remain in the " +
								"in-memory recovery write set of this process; retry Dispose or Save before process exit.");
						}
						TryReleaseProcessWindow(); // release authority only after local change-file writes complete
					}
				}
				finally { Monitor.Exit(_locker); }
				Debug.WriteLine($"{JsonFile.Name} saved to {JsonFile.Path}");
			}
			else if (!unflagged)
			{
				// Read-only pits perform no persistence, but their process ownership
				// (activity window) is still cleaned up on explicit disposal.
				TryReleaseProcessWindow();
			}
		}
		finally
		{
			StopConflictWatcher();
			ReleaseCanonicalPathOwnership();
			disposed = true;
		}
	}
	~Pit() => Dispose(false);
	#endregion
}
