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
	#region Load / Store / Save
	/// <summary>
	/// Maximum number of retry attempts when Load() encounters a JSON parse error
	/// (e.g. from reading a partially synced file).  Retries use exponential backoff:
	/// 200 ms, 1 s, 3 s, 5 s, 5 s.
	/// </summary>
	public static int MaxLoadRetries { get; set; } = 5;
	private static readonly int[] LoadRetryDelaysMs = { 200, 1000, 3000, 5000, 5000 };
	/// <summary>
	/// Loads the pit from disk under the agreed v3.13.2 read-during-write contract (CR003).
	/// <para>
	/// A candidate state is built and validated separately; live <see cref="HistoricItems"/>
	/// are replaced only after the entire canonical snapshot has been read and parsed
	/// successfully. When a canonical file known to exist is transiently missing, locked,
	/// incomplete, or unparsable during a rewrite, Load retries from disk without clearing
	/// the current in-memory state. A genuinely absent pit on initial creation remains a
	/// valid no-data case and returns false.
	/// </para>
	/// <para>
	/// Exhausting the bounded retry policy throws a descriptive
	/// <see cref="JsonPitPersistenceException"/>; Load never silently returns an empty or
	/// partially replaced pit.
	/// </para>
	/// </summary>
	public bool Load(bool undercover = false)
	{
		if (!JsonFile.Exists() && !canonicalSeen)
			return false; // genuinely absent on initial creation — valid no-data case
		canonicalSeen = true;
		var expectData = canonicalHadData || HistoricItems is { IsEmpty: false };
		Exception lastException = null;
		string lastFailure = null;
		for (int attempt = 0; attempt <= MaxLoadRetries; attempt++)
		{
			if (attempt > 0)
			{
				var delay = attempt - 1 < LoadRetryDelaysMs.Length
					? LoadRetryDelaysMs[attempt - 1]
					: LoadRetryDelaysMs[^1];
				Debug.WriteLine($"[JsonPit] Load retry {attempt}/{MaxLoadRetries} for {JsonFile.Name} after {delay} ms");
				Thread.Sleep(delay);
			}
			try
			{
				if (!JsonFile.Exists())
				{
					// Known-to-exist canonical transiently absent during a rewrite — retry.
					lastFailure = "canonical file transiently missing during a known rewrite";
					continue;
				}
				var textFile = new TextFile(JsonFile.FullName);
				var jsonArrayOfArrayOfObject = string.Join(Environment.NewLine, textFile.Read());
				bool emptyFile = string.IsNullOrEmpty(jsonArrayOfArrayOfObject) || jsonArrayOfArrayOfObject.Length < 2;
				if (emptyFile && expectData)
				{
					// The canonical file carried data before; empty content is a transient
					// truncation artifact of an in-place rewrite, never valid new state.
					lastFailure = "canonical file transiently empty during a known rewrite";
					continue;
				}
				for (int i = 0, square = 0; i < jsonArrayOfArrayOfObject.Length && i < 100 && square < 2; i++)
				{
					if (jsonArrayOfArrayOfObject[i] == '[') square++;
					else if (jsonArrayOfArrayOfObject[i] == '{')
						throw new FormatException("JSON file format is not compatible with JsonPit");
				}
				// Build and validate the complete candidate before publishing anything.
				var candidate = new ConcurrentDictionary<string, PitItems>(Comparer);
				if (!emptyFile)
					ParseHistoricItems(PitJson.ParseArray(jsonArrayOfArrayOfObject), candidate, DefaultMaxCount, markClean: true);
				stateGate.EnterWriteLock();
				try
				{
					FlushLiveItems();
					// Preserve accepted local edits, including previously unreported token
					// changes, across replacement of the on-disk snapshot.
					foreach (var pair in HistoricItems)
						foreach (var fragment in pair.Value.Fragments.Where(fragment => !fragment.Valid()))
						{
							var history = candidate.GetOrAdd(pair.Key, key => PitItems.Create(key, DefaultMaxCount));
							candidate[pair.Key] = history.Push(fragment);
						}
					HistoricItems = candidate;
					PublishAllLiveState();
				}
				finally { stateGate.ExitWriteLock(); }
				saveStateInitialized = true;
				lastLoadedUtc = DateTimeOffset.UtcNow;
				canonicalHadData = canonicalHadData || !emptyFile;
				Interlocked.Exchange(ref usingPersistence, 0);
				return true;
			}
			catch (InvalidOperationException) { throw; }  // not a transient error
			catch (FormatException) { throw; }             // structural incompatibility, not transient
			catch (Exception ex) when (ex is JsonReaderException or JsonException or System.IO.IOException)
			{
				lastException = ex;
				lastFailure = ex.Message;
				Debug.WriteLine($"[JsonPit] Load attempt {attempt} failed for {JsonFile.Name}: {ex.Message}");
				// continue to next retry
			}
		}
		Interlocked.Exchange(ref usingPersistence, 0);
		throw new JsonPitPersistenceException(
			$"JsonPit could not obtain a complete canonical snapshot of '{JsonFile.FullName}' after " +
			$"{MaxLoadRetries + 1} bounded read attempts ({lastFailure}). The previous in-memory state was " +
			"preserved and has not been cleared or partially replaced. The canonical file is expected to exist " +
			"because it was observed before; if an external process deleted or renamed it, operational repair is required.",
			lastException);
	}
	/// <summary>
	/// Parses a persisted pit JSON model into <paramref name="target"/> without touching
	/// any live state. Used for validated candidate loads and for JsonPit's private
	/// snapshot-reading mechanism (no second public <see cref="Pit"/> is constructed).
	/// Fragments reconstructed from a validated canonical pit represent already-persisted
	/// state and enter the candidate clean (<paramref name="markClean"/>); fragments from
	/// change files stay dirty because their canonical persistence follows the merge protocol.
	/// </summary>
	private static void ParseHistoricItems(JArray values, ConcurrentDictionary<string, PitItems> target, int maxCount, bool markClean = false)
	{
		foreach (JToken token in values)
		{
			switch (token)
			{
				case JObject obj:
					var single = new PitItem(obj);
					if (markClean) single.Validate();
					var store = target.GetOrAdd(single.Id, key => PitItems.Create(key, maxCount));
					target[single.Id] = store.Push(single);
					break;
				case JArray inner when inner.HasValues:
					if (inner.Any(element => element is not JObject))
						throw new FormatException("JSON file format is not compatible with JsonPit: history arrays must contain only objects");
					var q = (from o in inner.OfType<JObject>() select new PitItem(o)).ToList();
					if (q.Count == 0) break;
					var stack = PitItems.Create(q[^1].Id, maxCount);
					foreach (var item in q)
					{
						if (markClean) item.Validate();
						stack = stack.Push(item);
					}
					target.TryAdd(q[^1].Id, stack);
					break;
				case JArray:
					break;
				default:
					throw new FormatException($"JSON file format is not compatible with JsonPit: unsupported token type {token.Type}");
			}
		}
	}
	/// <summary>
	/// Persists the pit under the agreed v3.13.2 persistence boundary (CR003).
	/// <para>
	/// Takes brief exclusive access to the state/snapshot gate, waits for additions
	/// already inside the gate to finish, and captures one coherent point-in-time
	/// persistence snapshot. The gate is released before serialization, cloud-file I/O,
	/// and flag updates, so additions continue while the captured snapshot is persisted.
	/// </para>
	/// <para>
	/// Only fragments demonstrably included in the successfully persisted snapshot are
	/// validated; an addition accepted after the snapshot boundary keeps its per-fragment
	/// dirty state and is written by a later <see cref="Save"/>. Before snapshot fragments
	/// are marked clean, the newly persisted dirty fragments are recorded in the
	/// master-tenure recovery write set.
	/// </para>
	/// </summary>
	/// <returns>The canonical write or fallback publication completed for this snapshot.</returns>
	private PitSaveResult WriteCanonical(out ConcurrentDictionary<string, PitItems> persisted, bool pretty = false)
	{
		persisted = new(Comparer);
		FlushLiveItems();
		if (ReadOnly)
			throw new System.IO.IOException($"JsonFile {JsonFile.Name} was set to readonly mode but an attempt was made to execute JsonFile.Store");
		JsonFile.mkdir();
		// Brief exclusive snapshot window — clone fragments for byte stability, capture the
		// exact live dirty fragments included in this snapshot.
		// No I/O happens while the gate is held.
		List<List<PitItem>> model;
		List<PitItem> includedDirty;
		var snapshotHasData = false;
		stateGate.EnterWriteLock();
		try
		{
			var ordered = HistoricItems.OrderBy(kvp => kvp.Key, Comparer).ToList();
			model = new List<List<PitItem>>(ordered.Count);
			includedDirty = new List<PitItem>();
			foreach (var kvp in ordered)
			{
				var history = kvp.Value.Fragments;
				var clones = new List<PitItem>(history.Count);
				foreach (var fragment in history)
				{
					clones.Add(new PitItem(fragment)); // stable bytes: live JObjects stay mutable
					if (!fragment.Valid()) includedDirty.Add(fragment);
					snapshotHasData = true;
				}
				model.Add(clones);
				persisted[kvp.Key] = new PitItems(kvp.Key, clones, DefaultMaxCount);
			}
		}
		finally
		{
			stateGate.ExitWriteLock();
		}
		// Serialization and cloud-file I/O run outside the gate; additions may continue.
		var serializer = new JsonSerializer { DateFormatHandling = DateFormatHandling.IsoDateFormat };
		var rawJson = JToken.FromObject(model, serializer)
			.ToString(pretty ? Formatting.Indented : Formatting.None);
		// Write directly through the shared no-delete TextFile.Save path — the canonical
		// pathname never disappears; never use tmp-file-then-rename in cloud-synced areas.
		var pitFile = new TextFile(JsonFile.FullName);
		pitFile.Lines = [rawJson];
		pitFile.Changed = true;
		if (CheckClaimForSave() is { } fallback) return fallback;
		pitFile.Save();
		saveStateInitialized = true;
		canonicalSeen = true;
		canonicalHadData = canonicalHadData || snapshotHasData;
		if (!unflagged)
		{
			foreach (var fragment in includedDirty)
				RecordInRecoveryWriteSet(fragment);
		}
		// Validate only the fragments demonstrably included in the persisted snapshot.
		// A newer live fragment absent from the written snapshot stays dirty.
		foreach (var fragment in includedDirty)
			fragment.Validate();
		return new(PitSaveOutcome.Wrote);
	}

	public void Save(bool? backup = null, bool force = false) => SaveWithOutcome(backup, force);

	public PitSaveResult SaveWithOutcome(bool? backup = null, bool force = false)
	{
		var turn = BeginSave(backup, force);
		return turn.Completed ?? Store(turn.Ready, turn.Claimed);
	}

	private (PitSaveResult? Completed, DateTimeOffset Ready, DateTimeOffset Claimed) BeginSave(bool? backup, bool force)
	{
		lock (_locker)
		{
			if (ReadOnly) throw new System.IO.IOException($"Cannot save read-only pit {JsonFile.Name}.");
			if (disposed || HistoricItems == null || (!saveStateInitialized && HistoricItems.IsEmpty))
				return (new(PitSaveOutcome.PitNotLoaded), default, default);
			if (backup.HasValue) Backup = backup.Value;
			FlushLiveItems();
			if (!force && !Invalid() && !EnumerateChangeFiles().Any())
				return (new(PitSaveOutcome.NothingToWrite), default, default);
			var turn = TryAcquireMaster();
			if (!turn.Acquired) return (PitSaveResult.FromChanges(CreateChangeFiles()), default, default);
			return (null, turn.EarliestWriteUtc, claimTime);
		}
	}

	public Task SaveAsync(bool? backup = null, bool force = false) => SaveWithOutcomeAsync(backup, force);

	public async Task<PitSaveResult> SaveWithOutcomeAsync(bool? backup = null, bool force = false)
	{
		var turn = BeginSave(backup, force);
		if (turn.Completed is { } completed) return completed;
		var delay = turn.Ready - DateTimeOffset.UtcNow;
		if (delay > TimeSpan.Zero) await Task.Delay(delay).ConfigureAwait(false);
		return Store(turn.Ready, turn.Claimed);
	}

	private PitSaveResult? CheckClaimForSave()
	{
		if (unflagged) return null;
		var conflicts = MasterFlagFile.ConflictFlags(PitDir).ToList();
		var owners = conflicts.Select(f => (File: f, Owner: ReadFlagOwner(f))).ToList();
		if (OwnsClaim() && owners.All(f => f.Owner.Length != 0 && f.Owner != ExactProcessIdentity)) return null;
		var fallback = PitSaveResult.FromChanges(CreateChangeFiles());
		PublishRecoveryStatus(RecoveryStage.ConflictDetected, RecoveryRole.Loser, nameof(Store),
			"The claimed ticket changed, expired, or conflicted before canonical persistence.");
		foreach (var conflict in conflicts.Where(f => ReadFlagOwner(f) == ExactProcessIdentity))
			conflict.rm();
		return fallback;
	}

	private PitSaveResult Store(DateTimeOffset earliestWriteUtc, DateTimeOffset expectedClaim, PitMaintenanceResult result = null)
	{
		var delay = earliestWriteUtc - DateTimeOffset.UtcNow;
		if (delay > TimeSpan.Zero) Thread.Sleep((int)Math.Ceiling(delay.TotalMilliseconds));
		lock (_locker)
		{
			if (disposed || HistoricItems == null) return new(PitSaveOutcome.PitNotLoaded);
			if (claimTime != expectedClaim)
				return PitSaveResult.FromChanges(CreateChangeFiles());
			try { return StoreVerified(result); }
			catch (Exception ex) when (!unflagged)
			{
				PublishRecoveryStatus(RecoveryStage.Failed, RecoveryRole.Master, nameof(Store),
					"Canonical persistence failed. Recovering outstanding fragments.", exception: ex);
				ExportRecoveryUnion(nameof(Store));
				throw;
			}
		}
	}

	private PitSaveResult StoreVerified(PitMaintenanceResult result)
	{
		if (CheckClaimForSave() is { } fallback) return fallback;
		result ??= new PitMaintenanceResult(JsonFile.FullName, true) { CurrentMaster = true };
		var snapshot = ReadCanonicalSnapshot();
		var merged = MergePending(result, snapshot);
		stateGate.EnterWriteLock();
		try
		{
			FlushLiveItems();
			var dirty = HistoricItems.Values.SelectMany(history => history.Fragments)
				.Where(item => !item.Valid()).ToList();
			HistoricItems = snapshot;
			foreach (var item in dirty)
			{
				var history = HistoricItems.GetOrAdd(item.Id, key => PitItems.Create(key, DefaultMaxCount));
				HistoricItems[item.Id] = history.Push(item);
			}
			PublishAllLiveState();
		}
		finally { stateGate.ExitWriteLock(); }
		var written = WriteCanonical(out snapshot);
		if (written.Outcome != PitSaveOutcome.Wrote) return written;
		AccountAndClean(result, merged, snapshot);
		if (unflagged || ReturnClaim(DateTimeOffset.UtcNow))
		{
			recoveryWriteSet.Clear();
			hasMasterTenure = false;
		}
		else return new(PitSaveOutcome.Wrote, ExportRecoveryUnion("TicketLostAfterWrite"));
		return written;
	}
	#endregion
	#region Reload
	public bool Reload()
	{
		FlushLiveItems();
		ScanForConflictSignals(nameof(Reload)); // operation-boundary conflict scan (CR003)
		lock (_locker)
		{
			if (!MasterUpdatesAvailable() && !ForeignChangesAvailable() && !Invalid()) return false;
			if (Invalid() && !ReadOnly) CreateChangeFiles();
			Load();
			MergePending(new(JsonFile.FullName, false));
			return true;
		}
	}
	#endregion
}
