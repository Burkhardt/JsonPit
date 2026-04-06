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
	/// Add a PitItem as a new historic version using lock-free CAS algorithm.
	/// </summary>
	public bool Add(PitItem item)
	{
		while (true)
		{
			var currentStore = HistoricItems.GetOrAdd(item.Id, key => PitItems.Create(key, DefaultMaxCount));
			var top = currentStore.LatestFragment();
			if (top is not null && EqualsIgnoringModified(top, item)) return false;
			var newStore = currentStore.Push(item);
			if (HistoricItems.TryUpdate(item.Id, newStore, currentStore)) return true;
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
	public void Load(bool undercover = false)
	{
		if (!JsonFile.Exists()) return;
		var loadedOk = false;
		var hasData = false;
		try
		{
			var textFile = new TextFile(JsonFile.FullName);
			var jsonArrayOfArrayOfObject = string.Join(Environment.NewLine, textFile.Read());
			bool emptyFile = string.IsNullOrEmpty(jsonArrayOfArrayOfObject) || jsonArrayOfArrayOfObject.Length < 2;
			for (int i = 0, square = 0; i < jsonArrayOfArrayOfObject.Length && i < 100 && square < 2; i++)
			{
				if (jsonArrayOfArrayOfObject[i] == '[') square++;
				else if (jsonArrayOfArrayOfObject[i] == '{')
					throw new FormatException("JSON file format is not compatible with JsonPit");
			}
			HistoricItems = new ConcurrentDictionary<string, PitItems>(Comparer);
			if (!emptyFile) initValues(JArray.Parse(jsonArrayOfArrayOfObject));
			hasData = !emptyFile;
			loadedOk = true;
		}
		catch (InvalidOperationException) { throw; }
		finally
		{
			if (loadedOk && hasData && !(undercover || unflagged))
				ProcessFlag().Update(GetLatestItemChanged());
			Interlocked.Exchange(ref usingPersistence, 0);
		}
	}
	protected void Store(bool force = false, bool pretty = false, char indentChar = '\t')
	{
		if (HistoricItems is null) return;
		var jfExists = JsonFile.Exists();
		if (!jfExists && !HistoricItems.Any()) return;
		if (!jfExists || force || Invalid())
		{
			if (ReadOnly)
				throw new System.IO.IOException($"JsonFile {JsonFile.Name} was set to readonly mode but an attempt was made to execute JsonFile.Store");
			JsonFile.mkdir();
			// Write directly — never use tmp-file-then-rename in cloud-synced areas.
			var serializer = new JsonSerializer { DateFormatHandling = DateFormatHandling.IsoDateFormat };
			var rawJson = JToken.FromObject(GetRawPersistenceModel(), serializer)
				.ToString(pretty ? Formatting.Indented : Formatting.None);
			var pitFile = new TextFile(JsonFile.FullName);
			pitFile.Lines = [rawJson];
			pitFile.Changed = true;
			pitFile.Save();
			// TODO: Rainer — consider adding SetLastWriteTimeUtc to RaiFile
			if (!unflagged)
			{
				var changeTime = GetLatestItemChanged();
				MasterFlag().Update(changeTime);
				ProcessFlag().Update(changeTime);
			}
			foreach (var kvp in HistoricItems)
				kvp.Value.LatestFragment()?.Validate();
		}
	}
	private IReadOnlyList<IReadOnlyList<PitItem>> GetRawPersistenceModel() =>
		HistoricItems
			.OrderBy(kvp => kvp.Key, Comparer)
			.Select(kvp => (IReadOnlyList<PitItem>)kvp.Value.History)
			.ToList();
	public void Save(bool? backup = null, bool force = false)
	{
		if (backup is not null) Backup = backup.Value;
		if (ReadOnly)
			throw new System.IO.IOException($"JsonFile {JsonFile.Name} was set to readonly mode but an attempt was made to execute JsonFile.Save");
		Monitor.Enter(_locker);
		try
		{
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
	/// Find changes in memory vs disk and persist them as individual change files alongside the pit file.
	/// </summary>
	private void CreateChangeFiles()
	{
		var compareFile = new Pit(
			JsonFile.Path,
			undercover: true,
			unflagged: true,
			readOnly: true
		);
		var myLocalChanges = CompareToOtherHistory(compareFile.HistoricItems);
		if (myLocalChanges.Count == 0) return;
		// Already under _locker from Save() — merge directly
		HistoricItems = compareFile.HistoricItems;
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
		foreach (var changedPitItems in myLocalChanges)
			foreach (var fragment in changedPitItems)
				CreateChangeFile(fragment);
	}
	/// <summary>
	/// Writes a single PitItem as a change file alongside the pit file.
	/// Filename format: {ticks}_{machineName}.json
	/// </summary>
	public void CreateChangeFile(PitItem item, string server = null)
	{
		if (item is null) return;
		var ticks = item.Modified.UtcTicks.ToString();
		var machineName = server ?? Environment.MachineName;
		var fileName = $"{ticks}_{machineName}";
		var changeFile = new RaiFile(PitDir, fileName, "json");
		if (changeFile.Exists()) return;
		var json = new JArray(new JArray(item.DeepClone())).ToString(Formatting.None);
		var textFile = new TextFile(changeFile.FullName);
		textFile.Lines = [json];
		textFile.Changed = true;
		textFile.Save();
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
			var otherKeys = new HashSet<(string Id, DateTimeOffset Modified)>(
				otherItems.Select(item => (item.Id, item.Modified)));
			var missing = kvp.Value.Where(item => !otherKeys.Contains((item.Id, item.Modified))).ToList();
			if (missing.Count > 0)
				differences.Add(new PitItems(kvp.Key, missing, DefaultMaxCount));
		}
		return differences;
	}
	/// <summary>
	/// MergeChanges — the 6-step protocol:
	/// 1. Read the pit from disk to get what's current (may have changed since last load).
	/// 2. Diff the in-memory history against the disk version to find local changes.
	/// 3. Replace in-memory history with the disk version, then merge the local diff back in.
	/// 4. Read all change files and merge them into the in-memory history.
	/// 5. (Master only) Delete processed change files older than 10 minutes.
	/// 6. (Master only) Store the merged result as the new canonical pit file.
	/// Steps 5+6 are gated by <see cref="JsonPitBase.TryAcquireMaster"/> so only one machine
	/// writes the main file and cleans up change files at a time.
	/// </summary>
	public void MergeChanges()
	{
		if (!PitDir.Exists()) return;
		// Steps 1–4: everybody does this (both master and client)
		// 4. Read all change files and merge into history
		var processedChangeFiles = new List<RaiFile>();
		foreach (var file in EnumerateChangeFiles().OrderByDescending(x => x))
		{
			try
			{
				var changeJson = string.Join(Environment.NewLine, file.Read());
				if (string.IsNullOrWhiteSpace(changeJson))
					continue;

				var changePit = new Pit(
					JArray.Parse(changeJson),
					file.Path,
					readOnly: true,
					undercover: true,
					unflagged: true,
					autoload: false
				);
				foreach (var changeItems in changePit)
					MergeIntoHistory(changeItems);
				processedChangeFiles.Add(new RaiFile(file.FullName));
			}
			catch (InvalidOperationException) { }
		}
		// Gate: check master rights *after* merging (step 4) but *before* writing (steps 5+6)
		if (!ReadOnly && TryAcquireMaster())
		{
			// 5. Delete change files that have had time to propagate via cloud sync
			foreach (var rf in processedChangeFiles)
			{
				if (rf.FileAge.TotalSeconds > 600)
				{
					try { rf.rm(); }
					catch (Exception) { }
				}
			}
			// 6. Store the merged result as the new canonical pit file
			Store();
		}
	}
	public void MergeIntoHistory(PitItems changeItems)
	{
		while (true)
		{
			var currentStore = HistoricItems.GetOrAdd(changeItems.Key, key => PitItems.Create(key, DefaultMaxCount));
			var newStore = currentStore;
			foreach (var item in changeItems)
				newStore = newStore.Push(item);
			if (HistoricItems.TryUpdate(changeItems.Key, newStore, currentStore))
				break;
		}
	}
	#endregion
	#region Reload
	public bool Reload()
	{
		var masterUpdates = MasterUpdatesAvailable();
		var foreignChanges = ForeignChangesAvailable();
		if (masterUpdates && RunningOnMaster())
			throw new Exception($"Some process changed the main file without permission => inconsistent data in {nameof(Reload)}, file {JsonFile.Name}");
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
	private void initValues(JArray values)
	{
		foreach (JToken token in values)
		{
			switch (token)
			{
				case JObject obj:
					Add(new PitItem(obj));
					break;
				case JArray inner when inner.HasValues:
					if (inner.Any(element => element is not JObject))
						throw new FormatException("JSON file format is not compatible with JsonPit: history arrays must contain only objects");
					var q = (from o in inner.OfType<JObject>() select new PitItem(o)).ToList();
					if (q.Count == 0) break;
					var stack = PitItems.Create(q[^1].Id, DefaultMaxCount);
					foreach (var item in q) stack = stack.Push(item);
					HistoricItems.TryAdd(q[^1].Id, stack);
					break;
				case JArray:
					break;
				default:
					throw new FormatException($"JSON file format is not compatible with JsonPit: unsupported token type {token.Type}");
			}
		}
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
		orderBy = orderBy ?? (x => x.Id);
		this.descending = descending;
		HistoricItems = new ConcurrentDictionary<string, PitItems>();
		initValues(values);
		if (autoload)
		{
			if (JsonFile.Exists()) Load(undercover);
			MergeChanges();
		}
		if (string.IsNullOrEmpty(JsonFile.Name) || string.IsNullOrEmpty(JsonFile.Ext))
			throw new ArgumentException("JsonFile must have a valid name and extension - 3");
	}
	public Pit(JArray values, RaiPath pitDirectory, string subscriber = null,
		bool descending = false, bool readOnly = true, bool backup = false, bool undercover = false,
		bool unflagged = false, bool autoload = true, bool ignoreCase = false, string version = "")
		: this(pitDirectory, Enumerable.Empty<PitItems>(), subscriber, descending, readOnly,
			backup, undercover, unflagged, autoload, ignoreCase, version)
	{
		initValues(values);
		if (string.IsNullOrEmpty(JsonFile.Name) || string.IsNullOrEmpty(JsonFile.Ext))
			throw new ArgumentException("JsonFile must have a valid name and extension - 2");
	}
	/// <summary>
	/// Constructor for opening a Pit from a PitFile.
	/// </summary>
	public Pit(PitFile pitFile, bool readOnly = false)
		: base(readOnly, backup: false, unflagged: false, descending: false)
	{
		ArgumentNullException.ThrowIfNull(pitFile);
		JsonFile = pitFile;
		Subscriber = null;
		processIdentity = null;  // falls back to MachineName-ProcessName
		orderBy = x => x.Id;
		this.descending = false;
		HistoricItems = new ConcurrentDictionary<string, PitItems>();
		if (JsonFile.Exists()) Load(undercover: false);
		MergeChanges();
		if (string.IsNullOrEmpty(JsonFile.Name) || string.IsNullOrEmpty(JsonFile.Ext))
			throw new ArgumentException("JsonFile must have a valid name and extension - 1");
	}
	#endregion
	#region IDisposable
	public void Dispose()
	{
		Dispose(true);
		GC.SuppressFinalize(this);
	}
	protected virtual void Dispose(bool disposing)
	{
		if (disposed) return;
		if (disposing && !ReadOnly)
		{
			Save(backup: true, force: false);
			Debug.WriteLine($"{JsonFile.Name} saved to {JsonFile.Path}");
		}
		disposed = true;
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
