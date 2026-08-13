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
/// <summary>
/// JsonPit file container with item history and persistence.
/// Implements IDisposable to ensure changes are persisted on cleanup.
/// </summary>
public class Pit : JsonPitBase, IEnumerable<PitItems>, IDisposable
{
	private Func<PitItem, string> orderBy;
	public int DefaultMaxCount { get; }
	private bool disposed;
	/// <summary>
	/// State/snapshot gate (CR003, coordinated v3.13.2): concurrent <see cref="Add(PitItem)"/> calls
	/// enter shared mode and proceed in parallel; <see cref="Save"/> takes brief exclusive
	/// access to capture one coherent point-in-time persistence snapshot and releases the
	/// gate before serialization, cloud-file I/O, and flag updates.
	/// </summary>
	private readonly ReaderWriterLockSlim stateGate = new(LockRecursionPolicy.NoRecursion);
	/// <summary>Canonical path owned by this live public instance in the process-wide registry.</summary>
	private string ownedCanonicalPath;
	/// <summary>True once the canonical file has ever been observed/written by this instance — distinguishes a genuinely absent pit (valid no-data) from disappearance during a known rewrite.</summary>
	private bool canonicalSeen;
	/// <summary>True once the canonical file is known to have carried data — an empty read afterwards is a transient rewrite artifact, not valid state.</summary>
	private bool canonicalHadData;
	public override DateTimeOffset GetMemChanged() => GetLatestItemChanged();
	private StringComparer Comparer => ignoreCase ? StringComparer.InvariantCultureIgnoreCase : StringComparer.InvariantCulture;
	private StringComparison Comparison => ignoreCase ? StringComparison.InvariantCultureIgnoreCase : StringComparison.InvariantCulture;
	public void ConsiderCase()
	{
		if (!ignoreCase) return;
		ignoreCase = false;
		if (HistoricItems is not null)
			HistoricItems = new ConcurrentDictionary<string, PitItems>(HistoricItems, StringComparer.InvariantCulture);
	}
	public void IgnoreCase()
	{
		if (ignoreCase) return;
		ignoreCase = true;
		if (HistoricItems is not null)
			HistoricItems = new ConcurrentDictionary<string, PitItems>(HistoricItems, StringComparer.InvariantCultureIgnoreCase);
	}
	private bool ignoreCase;
	public ConcurrentDictionary<string, PitItems> HistoricItems = new();
	public ICollection<string> Keys => HistoricItems.Keys;
	public bool ContainsKey(string key) => HistoricItems.ContainsKey(key);
	public bool Contains(string itemId, bool withDeleted = false)
	{
		var isThere = HistoricItems.Keys.Contains(itemId, Comparer);
		if (withDeleted) return isThere;
		if (!isThere) return false;
		var top = HistoricItems[itemId].ProjectState();
		return top is { Deleted: false };
	}
	public bool Invalid() =>
		HistoricItems.Any(kvp => kvp.Value.LatestFragment() is { } latest && !latest.Valid());
	public DateTimeOffset GetLatestItemChanged()
	{
		var dates = from kvp in HistoricItems
					let latest = kvp.Value.LatestFragment()
					where latest is not null
					select latest.Modified;
		return dates.Any() ? dates.Max() : DateTimeOffset.MinValue;
	}
	public PitItem this[string key]
	{
		get
		{
			if (!HistoricItems.TryGetValue(key, out var list)) return null;
			var top = list.ProjectState();
			return top is { Deleted: false } ? top : null;
		}
	}
	public PitItem PitItem { set => Add(value); }
	public dynamic ItemProperty
	{
		set
		{
			var payload = NormalizeIdentityPayload((object)value);
			Add(new PitItem(payload));
		}
	}
	/// <summary>
	/// Add a PitItem as a new historic version using a lock-free CAS algorithm.
	/// <para>
	/// This is the live-mutation entry point: <paramref name="item"/>'s
	/// <see cref="PitItem.Modified"/> is refreshed to <c>UtcNow</c> immediately
	/// before the item is pushed onto the history stack — guaranteeing that
	/// every accepted fragment is stamped with the wall-clock instant of the
	/// insertion, regardless of any pre-existing <c>Modified</c> the caller
	/// may have inherited (from a copy ctor, a deserialized snapshot, etc.).
	/// </para>
	/// <para>
	/// Historical replay paths (loading from disk, merging change files,
	/// rebuilding from a <see cref="JArray"/>) must <em>not</em> route through
	/// here; they use <see cref="AddHistorical(PitItem)"/> so that original
	/// timestamps are kept intact.
	/// </para>
	/// </summary>
	public bool Add(PitItem item) => AddCore(item, refreshModified: true);
	/// <summary>
	/// Historical-replay variant of <see cref="Add(PitItem)"/>: pushes the
	/// item onto the history stack <em>without</em> refreshing its
	/// <see cref="PitItem.Modified"/>. Use this only for genuine historical
	/// data ingestion — loading from disk, replaying change files, importing
	/// a snapshot whose original timestamps must be preserved verbatim.
	/// <para>
	/// For all live in-process mutations call <see cref="Add(PitItem)"/>;
	/// it is the contract of <c>Add</c> that the stored fragment carries
	/// the wall-clock instant of insertion, not whatever <c>Modified</c>
	/// the caller's PitItem happened to inherit.
	/// </para>
	/// </summary>
	public bool AddHistorical(PitItem item) => AddCore(item, refreshModified: false);
	private bool AddCore(PitItem item, bool refreshModified)
	{
		// Shared entry into the state/snapshot gate: many additions may run concurrently;
		// only Save's brief snapshot capture excludes them. Additions are never serialized
		// behind cloud-file I/O.
		stateGate.EnterReadLock();
		try
		{
			var stamped = false;
			while (true)
			{
				var currentStore = HistoricItems.GetOrAdd(item.Id, key => PitItems.Create(key, DefaultMaxCount));
				var top = currentStore.LatestFragment();
				if (top is not null && EqualsIgnoringModified(top, item)) return false;
				if (refreshModified && !stamped)
				{
					item.Invalidate(); // one real UTC boundary per accepted insertion — not restamped on CAS retries
					stamped = true;
				}
				var newStore = currentStore.Push(item);
				if (ReferenceEquals(newStore, currentStore)) return false; // exact replay — idempotent
				if (HistoricItems.TryUpdate(item.Id, newStore, currentStore)) return true;
			}
		}
		finally
		{
			stateGate.ExitReadLock();
		}
	}
	public bool Add(string jsonObject) => Add(new PitItem(JObject.Parse(jsonObject)));
	public bool AddItems(IEnumerable<PitItem> items)
	{
		var result = true;
		foreach (var item in items) result &= Add(item);
		return result;
	}
	public bool AddItems(string jsonArray)
	{
		var jArray = JArray.Parse(jsonArray);
		return AddItems(jArray.Select(jObj => new PitItem((JObject)jObj)).ToList());
	}
	private static JObject NormalizeIdentityPayload(object value) =>
		value is JObject obj ? (JObject)obj.DeepClone() : JObject.FromObject(value);
	private static string GetIdentifier(JObject payload)
	{
		var itemId = (string)payload["Id"];
		if (string.IsNullOrWhiteSpace(itemId))
			throw new ArgumentException("Payload must contain Id.", nameof(payload));
		return itemId;
	}
	private static bool EqualsIgnoringModified(PitItem a, PitItem b)
	{
		var ja = (JObject)a.DeepClone();
		var jb = (JObject)b.DeepClone();
		ja.Remove("Modified");
		jb.Remove("Modified");
		return JToken.DeepEquals(ja, jb);
	}
	public bool Delete(string itemId, string by = null, bool backDate = true)
	{
		if (string.IsNullOrEmpty(itemId)) return true;
		try
		{
			var tombstone = new PitItem(itemId);
			if (tombstone.Delete(by, backDate))
				PitItem = tombstone;
		}
		catch (KeyNotFoundException) { }
		catch (Exception) { return false; }
		return true;
	}
	public bool RenameId(string oldKey, string newKey)
	{
		if (string.IsNullOrWhiteSpace(oldKey) || string.IsNullOrWhiteSpace(newKey)) return false;
		if (string.Equals(oldKey, newKey, Comparison)) return false;
		if (!Contains(oldKey) || Contains(newKey, withDeleted: true)) return false;
		var oldItem = this[oldKey];
		if (oldItem is null) return false;
		var newItem = new PitItem(oldItem);
		newItem.SetProperty(new { Id = newKey });
		return Delete(oldKey) && Add(newItem);
	}
	public JObject Get(string key, bool withDeleted = false)
	{
		if (!HistoricItems.TryGetValue(key, out var list)) return null;
		return withDeleted ? list.ProjectState(withDeleted: true) : (JObject)this[key];
	}
	public PitItem GetAt(string key, DateTimeOffset timestamp, bool withDeleted = false) =>
		HistoricItems.TryGetValue(key, out var list) ? list.ProjectState(timestamp, withDeleted) : null;
	public IEnumerable<KeyValuePair<DateTimeOffset, JToken>> ValuesOverTime(string oName, string pName)
	{
		if (!HistoricItems.TryGetValue(oName, out var list))
			return Enumerable.Empty<KeyValuePair<DateTimeOffset, JToken>>();
		return from item in list.History
			   select new KeyValuePair<DateTimeOffset, JToken>(item.Modified, item.Deleted ? null : item[pName]);
	}
	public IEnumerable<KeyValuePair<DateTimeOffset, List<JToken>>> ValueListsOverTime(string oName, string pName)
	{
		if (!HistoricItems.ContainsKey(oName))
			return Enumerable.Empty<KeyValuePair<DateTimeOffset, List<JToken>>>();
		return from kvp in ValuesOverTime(oName, pName)
			   select new KeyValuePair<DateTimeOffset, List<JToken>>(kvp.Key, ((JArray)kvp.Value).ToList<JToken>());
	}
	public IEnumerable<JObject> AllUndeleted()
	{
		foreach (var key in Keys)
			if (this[key] is { Deleted: false } item)
				yield return Get(key);
	}
	public void ExportJson(RaiPath exportFilePath, DateTimeOffset? at = null, bool pretty = true)
	{
		ExportJson(new RaiFile(exportFilePath, JsonFile.Name, "json"), at, pretty);
	}
	public void ExportJson(RaiFile exportFile, DateTimeOffset? at = null, bool pretty = true)
	{
		var exportItems = new JArray();
		foreach (var key in Keys)
		{
			var item = at is null ? this[key] : GetAt(key, at.Value, withDeleted: false);
			if (item is not null) exportItems.Add(item);
		}
		var formatting = pretty ? Formatting.Indented : Formatting.None;
		var textFile = new TextFile(exportFile.FullName)
		{
			Lines = [exportItems.ToString(formatting)],
			Changed = true
		};
		textFile.Save();
	}
	public IEnumerable<dynamic> AllUndeletedDynamic() =>
		AllUndeleted().Select(jObj =>
		{
			dynamic expando = new ExpandoObject();
			var dict = (IDictionary<string, object>)expando;
			foreach (var property in jObj.Properties())
			{
				dict[property.Name] = property.Value.Type is JTokenType.Object or JTokenType.Array
					? property.Value.ToObject<object>()
					: property.Value;
			}
			return expando;
		});
	public string Subscriber { get; private set; }
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
					ParseHistoricItems(JArray.Parse(jsonArrayOfArrayOfObject), candidate, DefaultMaxCount, markClean: true);
				stateGate.EnterWriteLock();
				try { HistoricItems = candidate; }
				finally { stateGate.ExitWriteLock(); }
				canonicalHadData = canonicalHadData || !emptyFile;
				if (!emptyFile && !(undercover || unflagged))
					ProcessFlag().Update();
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
	/// <returns>true when a canonical snapshot was written to disk.</returns>
	protected bool Store(bool force = false, bool pretty = false, char indentChar = '\t')
	{
		if (HistoricItems is null) return false;
		var jfExists = JsonFile.Exists();
		if (!jfExists && !HistoricItems.Any()) return false;
		if (jfExists && !force && !Invalid()) return false;
		if (ReadOnly)
			throw new System.IO.IOException($"JsonFile {JsonFile.Name} was set to readonly mode but an attempt was made to execute JsonFile.Store");
		JsonFile.mkdir();
		// Brief exclusive snapshot window — clone fragments for byte stability, capture the
		// exact live dirty fragments included in this snapshot and the snapshot change time.
		// No I/O happens while the gate is held.
		List<List<PitItem>> model;
		List<PitItem> includedDirty;
		var snapshotChangeTime = DateTimeOffset.MinValue;
		var snapshotHasData = false;
		stateGate.EnterWriteLock();
		try
		{
			var ordered = HistoricItems.OrderBy(kvp => kvp.Key, Comparer).ToList();
			model = new List<List<PitItem>>(ordered.Count);
			includedDirty = new List<PitItem>();
			foreach (var kvp in ordered)
			{
				var history = kvp.Value.History;
				var clones = new List<PitItem>(history.Count);
				foreach (var fragment in history)
				{
					clones.Add(new PitItem(fragment)); // stable bytes: live JObjects stay mutable
					if (!fragment.Valid()) includedDirty.Add(fragment);
					if (fragment.Modified > snapshotChangeTime) snapshotChangeTime = fragment.Modified;
					snapshotHasData = true;
				}
				model.Add(clones);
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
		pitFile.Save();
		canonicalSeen = true;
		canonicalHadData = canonicalHadData || snapshotHasData;
		if (!unflagged)
		{
			// Flag metadata describes the written snapshot, never newer live state.
			var changeTime = snapshotChangeTime == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : snapshotChangeTime;
			MasterFlag().Update(changeTime, originator: ExactProcessIdentity);
			ProcessFlag().Update(changeTime);
			// Record newly persisted fragments in the tenure recovery write set BEFORE they
			// are marked clean (concept §5).
			foreach (var fragment in includedDirty)
				RecordInRecoveryWriteSet(fragment);
		}
		// Validate only the fragments demonstrably included in the persisted snapshot.
		// A newer live fragment absent from the written snapshot stays dirty.
		foreach (var fragment in includedDirty)
			fragment.Validate();
		return true;
	}
	public void Save(bool? backup = null, bool force = false)
	{
		if (backup is not null) Backup = backup.Value;
		if (ReadOnly)
			throw new System.IO.IOException($"JsonFile {JsonFile.Name} was set to readonly mode but an attempt was made to execute JsonFile.Save");
		Monitor.Enter(_locker);
		try
		{
			ScanForConflictSignals(nameof(Save)); // operation-boundary conflict scan (CR003)
			if (TryAcquireMaster())
				Store(force);
			else
				CreateChangeFiles();
		}
		finally { Monitor.Exit(_locker); }
	}
	#endregion
	#region Change files
	/// <summary>
	/// Reads the canonical pit file into a private snapshot dictionary without
	/// constructing a second public <see cref="Pit"/> (CR003 §4: internal persistence
	/// comparison and merge mechanics use a private snapshot-reading mechanism).
	/// Returns an empty dictionary when no canonical content is readable.
	/// </summary>
	private ConcurrentDictionary<string, PitItems> ReadCanonicalSnapshot()
	{
		var snapshot = new ConcurrentDictionary<string, PitItems>(Comparer);
		if (!JsonFile.Exists()) return snapshot;
		try
		{
			var text = string.Join(Environment.NewLine, new TextFile(JsonFile.FullName).Read());
			if (string.IsNullOrEmpty(text) || text.Length < 2) return snapshot;
			ParseHistoricItems(JArray.Parse(text), snapshot, DefaultMaxCount, markClean: true);
		}
		catch (Exception ex) when (ex is JsonReaderException or JsonException or System.IO.IOException or FormatException)
		{
			Debug.WriteLine($"[JsonPit] ReadCanonicalSnapshot skipped unreadable canonical {JsonFile.Name}: {ex.Message}");
			return new ConcurrentDictionary<string, PitItems>(Comparer);
		}
		return snapshot;
	}
	/// <summary>
	/// Find changes in memory vs disk and persist them as individual change files alongside the pit file.
	/// </summary>
	private void CreateChangeFiles()
	{
		var diskSnapshot = ReadCanonicalSnapshot();
		var myLocalChanges = CompareToOtherHistory(diskSnapshot);
		if (myLocalChanges.Count == 0) return;
		// Already under _locker from Save() — merge directly
		stateGate.EnterWriteLock();
		try
		{
			HistoricItems = diskSnapshot;
			foreach (var changedPitItems in myLocalChanges)
			{
				HistoricItems.AddOrUpdate(
					changedPitItems.Key,
					changedPitItems,
					(_, existingFromDisk) =>
					{
						var merged = existingFromDisk;
						foreach (var fragment in changedPitItems)
							merged = merged.Push(fragment);
						return merged;
					}
				);
			}
		}
		finally { stateGate.ExitWriteLock(); }
		foreach (var changedPitItems in myLocalChanges)
			foreach (var fragment in changedPitItems)
				CreateChangeFile(fragment);
	}
	/// <summary>
	/// Writes a single fragment as an ordinary collision-safe change file alongside the
	/// pit file (CR003, coordinated v3.13.2).
	/// Filename: {Modified.UtcTicks}_{ExactProcessIdentity}_{Sha256}.json where Sha256 is
	/// the full lowercase SHA-256 of the exact canonical UTF-8 JSON payload. Repeating the
	/// same fragment produces the same filename and is idempotent; distinct equal-timestamp
	/// fragments produce distinct filenames and cannot suppress one another.
	/// </summary>
	/// <returns>The change file that now durably carries this fragment.</returns>
	public RaiFile CreateChangeFile(PitItem item, string server = null)
	{
		if (item is null) return null;
		var (canonicalPayload, sha) = ChangeFile.CanonicalPayloadFor(item);
		var identity = server ?? ExactProcessIdentity;
		var fileName = ChangeFile.ComposeName(item.Modified, identity, sha);
		var changeFile = new RaiFile(PitDir, fileName, "json");
		if (changeFile.Exists()) return changeFile; // idempotent republication
		changeFile.mkdir();
		// Exact-byte contract: the filename hash covers the exact canonical UTF-8 content,
		// so no line terminator is appended (Details of CR003 §12).
		System.IO.File.WriteAllText(changeFile.FullName, canonicalPayload, new System.Text.UTF8Encoding(false));
		changeFile.AwaitMaterializing();
		return changeFile;
	}
	private List<PitItems> CompareToOtherHistory(ConcurrentDictionary<string, PitItems> historicItems)
	{
		var differences = new List<PitItems>();
		foreach (var kvp in HistoricItems)
		{
			if (!historicItems.TryGetValue(kvp.Key, out var otherItems))
			{
				differences.Add(kvp.Value);
				continue;
			}
			// Content-aware fragment identity: (Id, Modified) alone would collapse distinct
			// equal-time fragments (CR003 §6).
			var otherKeys = new HashSet<(string Id, long Ticks, string Canonical)>(
				otherItems.Select(item => (item.Id, item.Modified.UtcTicks, OsLib.CanonicalJson.Canonicalize(item))));
			var missing = kvp.Value
				.Where(item => !otherKeys.Contains((item.Id, item.Modified.UtcTicks, OsLib.CanonicalJson.Canonicalize(item))))
				.ToList();
			if (missing.Count > 0)
				differences.Add(new PitItems(kvp.Key, missing, DefaultMaxCount));
		}
		return differences;
	}
	/// <summary>
	/// Grace period between a successful canonical persistence that accounts for a change
	/// file and that file's earliest permitted deletion (CR003: ten minutes measured from
	/// canonical persistence, not from original change-file creation). Settable for tests.
	/// </summary>
	public static TimeSpan ChangeFileCleanupGrace { get; set; } = TimeSpan.FromMinutes(10);
	/// <summary>
	/// In-memory cleanup-eligibility times per change file, recorded only after a canonical
	/// snapshot accounting for the file was successfully persisted. Restart or master change
	/// loses these times by design; the next master merges/persists again and starts a fresh
	/// grace period.
	/// </summary>
	private readonly ConcurrentDictionary<string, DateTimeOffset> changeFileCleanupEligibleAt = new(StringComparer.Ordinal);
	/// <summary>
	/// MergeChanges — the v3.13.2 protocol (CR003):
	/// 1. Read and hash/parse-validate all materialized change files; merge every valid file
	///    into the in-memory history (all participants — master and non-master).
	/// 2. (Exact master only) Persist the merged result as the new canonical pit file.
	/// 3. (Exact master only) Record cleanup eligibility for the files accounted for by that
	///    canonical save; a file becomes deletable only after a ten-minute propagation grace
	///    measured from the successful canonical persistence.
	/// 4. (Exact master only) On this and later passes, revalidate current exact-master
	///    authority and canonical health, then delete files whose grace elapsed.
	/// A file that fails hash or parse validation is not merged, not marked processed, and
	/// not deleted — it is reconsidered when it has materialized completely.
	/// </summary>
	public void MergeChanges()
	{
		if (!PitDir.Exists()) return;
		Monitor.Enter(_locker); // persistence/recovery gate
		try
		{
			MergeChangesUnderGate();
		}
		finally { Monitor.Exit(_locker); }
	}
	private void MergeChangesUnderGate()
	{
		var mergedFiles = new List<RaiFile>();
		foreach (var file in EnumerateChangeFiles().OrderByDescending(file => file.Name, StringComparer.Ordinal))
		{
			try
			{
				var payload = ChangeFile.ReadValidated(new RaiFile(file.FullName));
				if (payload is null)
					continue; // not yet materialized/hash-valid — reconsidered on a later pass
				var changeSnapshot = new ConcurrentDictionary<string, PitItems>(Comparer);
				ParseHistoricItems(payload, changeSnapshot, DefaultMaxCount);
				foreach (var changeItems in changeSnapshot.Values)
					MergeIntoHistory(changeItems);
				mergedFiles.Add(new RaiFile(file.FullName));
			}
			catch (Exception ex) when (ex is InvalidOperationException or JsonReaderException or JsonException or System.IO.IOException or FormatException)
			{
				Debug.WriteLine($"[JsonPit] MergeChanges skipped change file {file.Name}: {ex.Message}");
			}
		}
		// Gate: check exact-master rights *after* merging but *before* canonical writing.
		if (!ReadOnly && TryAcquireMaster())
		{
			// Canonical-save-before-delete ordering: only a canonical snapshot that
			// accounts for a merged fragment may start that file's cleanup grace.
			var canonicalSaved = false;
			try
			{
				canonicalSaved = Store();
			}
			catch (Exception ex)
			{
				PublishRecoveryStatus(RecoveryStage.Failed, RecoveryRole.Master, nameof(MergeChanges),
					$"Canonical persistence after merge failed; change files remain untouched: {ex.Message}",
					fileCount: mergedFiles.Count, exception: ex);
				throw;
			}
			var now = DateTimeOffset.UtcNow;
			// The canonical accounts for a merged file when it was just persisted, or when
			// every merged fragment was an exact replay already present in a clean canonical.
			var accountedFor = canonicalSaved || (JsonFile.Exists() && !Invalid());
			if (accountedFor)
			{
				var newlyEligible = 0;
				foreach (var rf in mergedFiles)
					if (changeFileCleanupEligibleAt.TryAdd(rf.FullName, now))
						newlyEligible++;
				if (newlyEligible > 0)
					PublishRecoveryStatus(RecoveryStage.CleanupPending, RecoveryRole.Master, nameof(MergeChanges),
						$"{newlyEligible} merged change file(s) canonicalized; cleanup grace of {ChangeFileCleanupGrace} started.",
						fileCount: newlyEligible);
			}
			// Later cleanup pass: revalidate exact authority + canonical health before deleting.
			CleanupEligibleChangeFiles(now);
		}
	}
	/// <summary>
	/// Deletes change files whose post-canonical-save grace has elapsed. Current-master-only;
	/// revalidates exact master authority and canonical health before each deletion pass.
	/// </summary>
	private void CleanupEligibleChangeFiles(DateTimeOffset now)
	{
		if (changeFileCleanupEligibleAt.IsEmpty) return;
		if (unflagged == false && MasterFlag().Originator != ExactProcessIdentity) return;
		if (!JsonFile.Exists()) return; // canonical health check
		foreach (var kvp in changeFileCleanupEligibleAt)
		{
			if (now - kvp.Value < ChangeFileCleanupGrace) continue;
			var rf = new RaiFile(kvp.Key);
			try
			{
				if (rf.Exists()) rf.rm();
				changeFileCleanupEligibleAt.TryRemove(kvp.Key, out _);
			}
			catch (Exception) { }
		}
	}
	public void MergeIntoHistory(PitItems changeItems)
	{
		stateGate.EnterReadLock();
		try
		{
			while (true)
			{
				var currentStore = HistoricItems.GetOrAdd(changeItems.Key, key => PitItems.Create(key, DefaultMaxCount));
				var newStore = currentStore;
				foreach (var item in changeItems)
					newStore = newStore.Push(item);
				if (ReferenceEquals(newStore, currentStore))
					break; // exact replay duplicates are harmless and ignored
				if (HistoricItems.TryUpdate(changeItems.Key, newStore, currentStore))
					break;
			}
		}
		finally { stateGate.ExitReadLock(); }
	}
	#endregion
	#region Reload
	public bool Reload()
	{
		ScanForConflictSignals(nameof(Reload)); // operation-boundary conflict scan (CR003)
		var masterUpdates = MasterUpdatesAvailable();
		var foreignChanges = ForeignChangesAvailable();
		if (masterUpdates && RunningOnMaster())
		{
			// v3.13.2 exact-PID ownership: the recorded master IS this exact process, so a
			// newer master-flag time reflects this process's own earlier canonical write
			// (for example before a dispose/reopen), not unauthorized foreign interference.
			// A different PID writing the canonical would be recorded as a different owner
			// and is handled by the non-master path below.
			Load();
			if (foreignChanges) MergeChanges();
			return true;
		}
		if (masterUpdates) { Save(); Load(); return true; }
		if (foreignChanges) { MergeChanges(); return true; }
		if (Invalid()) { Save(); return true; }
		return false;
	}
	#endregion
	#region IEnumerable
	IEnumerator IEnumerable.GetEnumerator()
	{
		foreach (var item in HistoricItems) yield return item.Value;
	}
	public IEnumerator<PitItems> GetEnumerator()
	{
		foreach (var kvp in HistoricItems) yield return kvp.Value;
	}
	#endregion
	#region Init
	private void initValues(JArray values) =>
		// Historical replay: preserves original Modified timestamps; uses the same
		// parser as validated candidate loads and private snapshot reads.
		ParseHistoricItems(values, HistoricItems, DefaultMaxCount);
	private void initValues(IEnumerable<PitItems> values)
	{
		if (values is null) return;
		foreach (var pitItems in values.Where(pi => pi.Count > 0))
		{
			var q = (from o in pitItems select new PitItem(o)).ToList();
			var stack = PitItems.Create(q[^1].Id, DefaultMaxCount);
			foreach (var item in q) stack = stack.Push(item);
			HistoricItems.TryAdd(q[^1].Id, stack);
		}
	}
	#endregion
	#region Constructors
	public Pit(RaiPath pitDirectory, IEnumerable<PitItems> values = null, string subscriber = null,
		bool descending = false, bool readOnly = true, bool backup = false, bool undercover = false,
		bool unflagged = false, bool autoload = true, bool ignoreCase = false, string version = "")
		: base(readOnly, backup, unflagged, descending)
	{
		if (pitDirectory is null || pitDirectory.ToString().Length < 3)
			throw new ArgumentException("pitDirectory must be a valid PitDirectory");
		string[] segments = pitDirectory.ToString().Split(Os.DIR, StringSplitOptions.RemoveEmptyEntries);
		if (segments.Length == 0)
			throw new ArgumentException("pitDirectory must contain at least one valid segment");
		JsonFile = new PitFile(pitDirectory, name: segments[^1]);
		Subscriber = subscriber;
		processIdentity = subscriber;
		RegisterCanonicalPathOwnership(); // reject a duplicate before it can load or mutate state
		orderBy = orderBy ?? (x => x.Id);
		this.descending = descending;
		HistoricItems = new ConcurrentDictionary<string, PitItems>();
		try
		{
			initValues(values);
			if (autoload)
			{
				if (JsonFile.Exists()) Load(undercover);
				MergeChanges();
			}
			if (string.IsNullOrEmpty(JsonFile.Name) || string.IsNullOrEmpty(JsonFile.Ext))
				throw new ArgumentException("JsonFile must have a valid name and extension - 3");
			StartConflictWatcher();
			ScanForConflictSignals("Construction");
		}
		catch
		{
			TryReleaseProcessWindow();
			ReleaseCanonicalPathOwnership(); // constructor failure must not leave stale ownership
			throw;
		}
	}
	public Pit(JArray values, RaiPath pitDirectory, string subscriber = null,
		bool descending = false, bool readOnly = true, bool backup = false, bool undercover = false,
		bool unflagged = false, bool autoload = true, bool ignoreCase = false, string version = "")
		: this(pitDirectory, Enumerable.Empty<PitItems>(), subscriber, descending, readOnly,
			backup, undercover, unflagged, autoload, ignoreCase, version)
	{
		try
		{
			initValues(values);
			if (string.IsNullOrEmpty(JsonFile.Name) || string.IsNullOrEmpty(JsonFile.Ext))
				throw new ArgumentException("JsonFile must have a valid name and extension - 2");
		}
		catch
		{
			TryReleaseProcessWindow();
			ReleaseCanonicalPathOwnership();
			throw;
		}
	}
	/// <summary>
	/// Constructor for opening a Pit from a PitFile.
	/// </summary>
	public Pit(PitFile pitFile, bool readOnly = false)
		: this(pitFile, subscriber: null, readOnly)
	{
	}
	/// <summary>
	/// Constructor for opening a Pit from a PitFile with an explicit process identity.
	/// </summary>
	public Pit(PitFile pitFile, string subscriber, bool readOnly = false)
		: base(readOnly, backup: false, unflagged: false, descending: false)
	{
		ArgumentNullException.ThrowIfNull(pitFile);
		JsonFile = pitFile;
		Subscriber = subscriber;
		processIdentity = subscriber;
		RegisterCanonicalPathOwnership(); // reject a duplicate before it can load or mutate state
		orderBy = x => x.Id;
		this.descending = false;
		HistoricItems = new ConcurrentDictionary<string, PitItems>();
		try
		{
			if (JsonFile.Exists()) Load(undercover: false);
			MergeChanges();
			if (string.IsNullOrEmpty(JsonFile.Name) || string.IsNullOrEmpty(JsonFile.Ext))
				throw new ArgumentException("JsonFile must have a valid name and extension - 1");
			StartConflictWatcher();
			ScanForConflictSignals("Construction");
		}
		catch
		{
			TryReleaseProcessWindow();
			ReleaseCanonicalPathOwnership(); // constructor failure must not leave stale ownership
			throw;
		}
	}
	#endregion
	#region Instance registry (CR003 §4)
	// Weak references: the registry must not keep abandoned instances reachable, so a
	// leaked, collected Pit stops counting as the live owner of its canonical path.
	private static readonly ConcurrentDictionary<string, WeakReference<Pit>> livePublicInstances = new(StringComparer.Ordinal);
	/// <summary>
	/// Reserves process-wide ownership of this instance's canonical pit path before the
	/// constructor can load flags, merge, or mutate state. A second live public instance
	/// (writable or read-only) for an already-owned canonical path is rejected with a
	/// descriptive <see cref="PitInstanceConflictException"/>.
	/// </summary>
	private void RegisterCanonicalPathOwnership()
	{
		var canonicalPath = JsonFile.FullName;
		var candidate = new WeakReference<Pit>(this);
		while (true)
		{
			if (livePublicInstances.TryAdd(canonicalPath, candidate))
				break;
			if (livePublicInstances.TryGetValue(canonicalPath, out var existing))
			{
				if (existing.TryGetTarget(out var owner) && !owner.disposed)
					throw new PitInstanceConflictException(canonicalPath);
				// Stale entry of a collected or disposed instance — replace it.
				if (livePublicInstances.TryUpdate(canonicalPath, candidate, existing))
					break;
			}
		}
		ownedCanonicalPath = canonicalPath;
	}
	/// <summary>
	/// Releases only this instance's own path reservation; safe to call repeatedly and
	/// after constructor failure.
	/// </summary>
	private void ReleaseCanonicalPathOwnership()
	{
		var owned = ownedCanonicalPath;
		if (owned is null) return;
		if (livePublicInstances.TryGetValue(owned, out var existing) &&
			existing.TryGetTarget(out var owner) && ReferenceEquals(owner, this))
			livePublicInstances.TryRemove(new KeyValuePair<string, WeakReference<Pit>>(owned, existing));
		ownedCanonicalPath = null;
	}
	#endregion
	#region Recovery (CR003 + JsonPit-CONCEPT-Live-Split-Master-Recovery)
	/// <summary>Debounce/materialization delay between a watcher signal and its queued recovery evaluation. Settable for tests.</summary>
	public static TimeSpan RecoveryDebounce { get; set; } = TimeSpan.FromSeconds(1);
	/// <summary>Extra safety grace after a named claimant's process-window expiry before the exact current master may retire an orphaned longer conflict flag. Settable for tests.</summary>
	public static TimeSpan OrphanedConflictFlagGrace { get; set; } = TimeSpan.FromMinutes(10);
	/// <summary>
	/// In-memory recovery write set for the current exact-process master tenure
	/// (concept §5). Keyed by the fragment's collision-safe change-file name, which
	/// deduplicates exact replays by fragment identity and canonical content. Entries are
	/// removed per fragment once an ordinary change file is locally written, materialized,
	/// hash-verified, and parsed — no cloud or master acknowledgement is required.
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
		recoveryWriteSet.TryAdd(ChangeFile.ComposeName(fragment, ExactProcessIdentity), fragment);
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
	/// collision-safe change-file identity.
	/// </summary>
	private Dictionary<string, PitItem> SnapshotRecoveryUnion()
	{
		var union = new Dictionary<string, PitItem>(StringComparer.Ordinal);
		stateGate.EnterWriteLock();
		try
		{
			foreach (var entry in recoveryWriteSet)
				union[entry.Key] = entry.Value;
			foreach (var kvp in HistoricItems)
				foreach (var fragment in kvp.Value.History)
					if (!fragment.Valid())
						union[ChangeFile.ComposeName(fragment, ExactProcessIdentity)] = fragment;
		}
		finally { stateGate.ExitWriteLock(); }
		return union;
	}
	/// <summary>
	/// Publishes fragments as ordinary collision-safe change files and transfers recovery
	/// responsibility per fragment: an entry leaves the write set only after its file is
	/// locally written, materialized, hash-verified, and parsed. Failed fragments retain
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
	private void ExportRecoveryUnion(string operation)
	{
		var union = SnapshotRecoveryUnion();
		if (union.Count == 0) return;
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
	}
	private RecoveryRole CurrentRecoveryRole()
	{
		if (unflagged) return RecoveryRole.None;
		try
		{
			return MasterFlag().Originator == ExactProcessIdentity ? RecoveryRole.Master : RecoveryRole.Observer;
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
		if (!unflagged)
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
			var role = canonicalOwner == ExactProcessIdentity
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
			return new TimestampedValue(flag.Lines[0]).Value ?? string.Empty;
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
	/// currently dirty fragments as ordinary collision-safe change files, optionally
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
						Store(force: false);
					}
					else
					{						// Change files are published before the optional canonical save so the
						// fragments remain recoverable if that canonical save fails.
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
						try
						{
							if (TryAcquireMaster())
								Store();
						}
						catch (Exception ex)
						{
							// Fragments are already durable as change files; another master can merge them.
							PublishRecoveryStatus(RecoveryStage.DeferredForRetry, CurrentRecoveryRole(), nameof(Dispose),
								$"Optional canonical save during disposal failed after change files were durable: {ex.Message}",
								exception: ex);
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
#region Obsolete Class Item
/// <summary>
/// Base item with modified tracking and dirty state management.
/// Use PitItem instead — it provides the same functionality backed by JObject with full JSON support.
/// </summary>
[Obsolete("Use PitItem instead. PitItem extends JObject and supports the same Id/Modified/Deleted/Note properties " +
	"plus JSON merge, extend, and projection capabilities. Construct via new PitItem(id) or new PitItem(jObject).")]
public class Item : ICloneable
{
	public string Id { get; set; }
	public DateTimeOffset Modified { get; internal set; }
	public virtual DateTimeOffset Changed() => Modified;
	public bool Deleted { get; set; }
	public bool Delete(string by = null, bool backDate100 = true)
	{
		if (!Deleted)
		{
			Deleted = true;
			if (backDate100)
				Modified = DateTimeOffset.UtcNow - new TimeSpan(0, 0, 0, 100);
			Invalidate();
			var s = $"[{Modified.ToUniversalTime():u}] deleted";
			if (!string.IsNullOrEmpty(by)) s += " by " + by;
			Note = s + ";\n" + Note;
		}
		return true;
	}
	protected bool Dirty { get; set; }
	public virtual bool Valid() => !Dirty;
	public virtual void Validate() => Dirty = false;
	public virtual void Invalidate()
	{
		Dirty = true;
		Modified = DateTimeOffset.UtcNow;
	}
	public string Note { get; set; }
	public override string ToString() => JSON.Serialize<Item>(this);
	public virtual bool Matches(Item x) => x.Id == Id;
	public virtual bool Matches(SearchExpression se) => se.IsMatch(this);
	public virtual bool Matches(string filter, Compare comp = Compare.ByProperty)
	{
		if (comp == Compare.JSON)
		{
			if (string.IsNullOrWhiteSpace(filter)) return true;
			var json = ToString();
			return filter.Split(['+', ' ']).All(f => json.Contains(f));
		}
		return new SearchExpression(filter).IsMatch(this);
	}
	public T Clone<T>()
	{
		var s = JSON.SerializeDynamic(this, JsonPitBase.jilOptions);
		return JSON.Deserialize<T>(s, JsonPitBase.jilOptions);
	}
	public virtual dynamic Clone()
	{
		var s = JSON.SerializeDynamic(this, JsonPitBase.jilOptions);
		if (GetType().FullName.Contains("Dynamic"))
			return JSON.DeserializeDynamic(s, JsonPitBase.jilOptions);
		var settings = new JsonSerializerSettings
		{
			DateFormatHandling = DateFormatHandling.IsoDateFormat,
			DateParseHandling = DateParseHandling.DateTimeOffset,
			DateTimeZoneHandling = DateTimeZoneHandling.Utc
		};
		return JsonConvert.DeserializeObject(s, settings);
	}
	public virtual void Merge(Item second)
	{
		if (Id != second.Id)
			throw new ArgumentException($"Error: {Id}.Merge({second.Id}) is an invalid call - Ids must be equal.");
		if (Changed().UtcTicks == second.Changed().UtcTicks) { Dirty = false; return; }
		if (Changed().UtcTicks <= second.Changed().UtcTicks)
		{
			Dirty = true;
			Modified = second.Modified;
			if (second.Deleted) { Dirty = Dirty || Deleted != second.Deleted; Deleted = true; }
			else Deleted = false;
			foreach (var prop in GetType().GetProperties())
			{
				if (!prop.CanWrite) continue;
				try { prop.SetValue(this, prop.GetValue(second, null), null); }
				catch (System.Reflection.TargetParameterCountException)
				{
					try { prop.SetValue(this, prop.GetValue(this, null), null); }
					catch (System.Reflection.TargetParameterCountException) { }
				}
			}
		}
		else Dirty = true;
	}
	public Item(string id, string comment, bool invalidate = true)
	{
		Id = id;
		Note = comment;
		if (invalidate) Invalidate();
	}
	public Item(Item from)
	{
		var clone = from.Clone();
		foreach (var prop in GetType().GetProperties())
			if (prop.CanWrite)
				prop.SetValue(this, prop.GetValue(clone, null), null);
		Modified = from.Changed();
	}
	public Item() { }
}
#endregion 
