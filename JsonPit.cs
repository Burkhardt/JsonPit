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
public partial class Pit : JsonPitBase, IEnumerable<PitItems>, IDisposable
{
	private static long lastLiveMutationUtcTicks;
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
		=> SetIgnoreCase(false);
	public void IgnoreCase()
		=> SetIgnoreCase(true);
	private void SetIgnoreCase(bool value)
	{
		stateGate.EnterWriteLock();
		try
		{
			if (ignoreCase == value) return;
			FlushLiveItems();
			var comparison = value ? StringComparer.InvariantCultureIgnoreCase : StringComparer.InvariantCulture;
			var histories = new ConcurrentDictionary<string, PitItems>(HistoricItems, comparison);
			var live = new ConcurrentDictionary<string, LiveSlot>(liveItems, comparison);
			HistoricItems = histories;
			liveItems = live;
			ignoreCase = value;
		}
		finally { stateGate.ExitWriteLock(); }
	}
	private bool ignoreCase;
	/// <summary>
	/// Legacy engine-level history store. Application mutations must use Add,
	/// AddHistorical or MergeIntoHistory; direct dictionary edits bypass live tracking.
	/// Fragment values exposed by PitItems.History are detached snapshots.
	/// </summary>
	public ConcurrentDictionary<string, PitItems> HistoricItems = new();
	private bool saveStateInitialized = true;
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
		HistoricItems.Any(kvp => kvp.Value.Fragments.Any(fragment => !fragment.Valid()));
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
			if (!liveItems.TryGetValue(key, out var slot)) return null;
			var top = slot.Current?.Item;
			return top is { Deleted: false } ? top : null;
		}
	}
	public PitItem PitItem { set => Add(value); }
	public dynamic ItemProperty
	{
		set
		{
			var payload = NormalizeIdentityPayload((object)value);
			PitItem.ValidateClientPayload(payload);
			Add(new PitItem(payload));
		}
	}
	/// <summary>
	/// Accept a sparse fragment using per-identity synchronization and CAS publication.
	/// A new identity adopts the supplied item and its nested objects as the live graph;
	/// history owns an independent snapshot. Later fragments update that live graph.
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
	private bool AddCore(PitItem item, bool refreshModified, bool legacyTombstone = false)
	{
		if (item is null) throw new ArgumentNullException(nameof(item));
		// CR077: historical replay must not bypass live ownership. Import detached
		// historical fragments rather than attaching another Pit's live graph.
		if (item.LiveBinding is { } owned && !ReferenceEquals(owned.Owner, this))
			throw new InvalidOperationException("A live PitItem cannot be attached to another Pit. Import a detached payload instead.");
		if (item.LiveBinding is { } live && refreshModified)
		{
			if (!ReferenceEquals(live.Owner, this))
				throw new InvalidOperationException("A live PitItem cannot be attached to another Pit. Import a detached payload instead.");
			return WithLiveState(live.Id, slot =>
			{
				EnsureLiveOwner(live);
				return FlushFallback(slot);
			});
		}
		if (refreshModified && !legacyTombstone) item.EnsureValidForLiveAdd();
		return WithLiveState(item.Id, slot =>
		{
			FlushFallback(slot);
			return AppendFragment(slot, item, refreshModified, legacyTombstone);
		});
	}
	private static DateTimeOffset NextLiveMutationTimestamp()
	{
		while (true)
		{
			var observed = Volatile.Read(ref lastLiveMutationUtcTicks);
			var now = DateTimeOffset.UtcNow.UtcTicks;
			var candidate = Math.Max(now, observed + 1);
			if (Interlocked.CompareExchange(ref lastLiveMutationUtcTicks, candidate, observed) == observed)
				return new DateTimeOffset(candidate, TimeSpan.Zero);
		}
	}
	public bool Add(string jsonObject)
	{
		var payload = PitJson.ParseObject(jsonObject);
		PitItem.ValidateClientPayload(payload);
		return Add(new PitItem(payload));
	}
	public bool AddItems(IEnumerable<PitItem> items)
	{
		if (items is null) throw new ArgumentNullException(nameof(items));
		var batch = items.ToList();
		foreach (var item in batch)
		{
			if (item is null) throw new ArgumentException("Batch entities cannot be null.", nameof(items));
			item.EnsureValidForLiveAdd();
		}
		var result = true;
		foreach (var item in batch) result &= Add(item);
		return result;
	}
	public bool AddItems(string jsonArray)
	{
		var jArray = PitJson.ParseArray(jsonArray);
		foreach (var token in jArray)
		{
			if (token is not JObject payload) throw new ArgumentException("Batch entities must be JSON objects.");
			PitItem.ValidateClientPayload(payload);
		}
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
		static JObject Payload(PitItem item) =>
			new(item.Properties().Where(p => p.Name != nameof(item.Modified)));
		return JToken.DeepEquals(Payload(a), Payload(b));
	}
	public bool Delete(string itemId, string by = null, bool backDate = true)
	{
		if (string.IsNullOrEmpty(itemId)) return true;
		try
		{
			// Only the engine-owned tombstone of an existing record may bypass live ID validation.
			var existing = Contains(itemId, withDeleted: true);
			if (!existing) PitItem.ValidateLiveId(itemId);
			var tombstone = new PitItem(itemId);
			if (tombstone.Delete(by, backDate))
				AddCore(tombstone, refreshModified: true, legacyTombstone: existing);
		}
		catch (KeyNotFoundException) { }
		catch (Exception) { return false; }
		return true;
	}
	public bool RenameId(string oldKey, string newKey)
	{
		PitItem.ValidateLiveId(newKey);
		if (string.IsNullOrWhiteSpace(oldKey) || string.IsNullOrWhiteSpace(newKey)) return false;
		if (string.Equals(oldKey, newKey, Comparison)) return false;
		if (!Contains(oldKey) || Contains(newKey, withDeleted: true)) return false;
		var oldItem = this[oldKey];
		if (oldItem is null) return false;
		var newItem = PitItem.CreateEngineMutationCopy(oldItem, newKey);
		return Delete(oldKey) && Add(newItem);
	}
	public JObject Get(string key, bool withDeleted = false)
	{
		if (!HistoricItems.TryGetValue(key, out var list)) return null;
		return this[key] ?? (withDeleted ? list.ProjectState(withDeleted: true) : null);
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
				yield return item;
	}
	public void ExportJson(RaiPath exportFilePath, DateTimeOffset? at = null, bool pretty = true)
	{
		ExportJson(new RaiFile(exportFilePath, JsonFile.Name, "json"), at, pretty);
	}
	public void ExportJson(RaiFile exportFile, DateTimeOffset? at = null, bool pretty = true)
	{
		if (at is null) FlushLiveItems();
		var exportItems = new JArray();
		foreach (var key in Keys)
		{
			var item = at is null ? this[key] : GetAt(key, at.Value, withDeleted: false);
			if (item is not null) exportItems.Add(item.DeepClone());
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
	private void initValues(JArray values)
	{
		// Historical replay: preserves original Modified timestamps; uses the same
		// parser as validated candidate loads and private snapshot reads.
		ParseHistoricItems(values, HistoricItems, DefaultMaxCount);
		PublishAllLiveState();
	}
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
		PublishAllLiveState();
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
		this.ignoreCase = ignoreCase;
		HistoricItems = new ConcurrentDictionary<string, PitItems>(Comparer);
		liveItems = new ConcurrentDictionary<string, LiveSlot>(Comparer);
		try
		{
			initValues(values);
			saveStateInitialized = autoload;
			if (autoload)
			{
				if (JsonFile.Exists()) Load(undercover);
				MergePending(new(JsonFile.FullName, false));
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
			MergePending(new(JsonFile.FullName, false));
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
}
