using Jil;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using RaiUtils;
using OsLib;
using System.Dynamic;
using System.Collections.Immutable;
using System.Net.Mime;
///TODO: ChangeFile treatment
/// s.#1318 
namespace JsonPit
{
	/// <summary>
	/// Base container holding a key identifier for item groups.
	/// </summary>
	public class ItemsBase
	{
		/// <summary>Identifying id, i.e. JsonFileName from enclosing JsonFile</summary>
		public string Key;
		public ItemsBase(string key = null)
		{
			this.Key = key;
		}
	}

	/// <summary>
	/// Common base for pits with config, flags, and persistence helpers.
	/// </summary>
	public class JsonPitBase
	{
		public static string Version
		{
			get
			{
				if (version == null)
				{
					var asm = System.Reflection.Assembly.GetExecutingAssembly();
					version = asm.GetName().Version.ToString(2);
				}
				return version;
			}
			set
			{
				version = value;
			}
		}
		private static string version = null;

		public static RaiPath ConfigDefaultDir
		{
			get
			{
				if (configDirDefault == null)
					configDirDefault = Os.CloudStorageRootDir / "Config";
				return configDirDefault;
			}
			set
			{
				configDirDefault = value;
			}
		}
		private static RaiPath configDirDefault = null;

		public static Options jilOptions = new Options(prettyPrint: true, excludeNulls: false, jsonp: false, dateFormat: DateTimeFormat.ISO8601, includeInherited: true);

		#region Semaphore
		protected int usingPersistence = 0;                // instance-level: each Pit guards its own persistence
		protected readonly object _locker = new object();   // instance-level: each Pit has its own lock
		#endregion

		#region Flag file
		/// <summary>
		/// Can be used to identify if the current server is master for this JsonFile
		/// </summary>
		/// <returns>true if current server has master rights to the file</returns>
		public bool RunningOnMaster()
		{
			return unflagged || MasterFlag().Originator == Environment.MachineName;
		}
		protected bool unflagged;

		public ProcessFlagFile ProcessFlag()
		{
			if (fileFlag == null)
				fileFlag = new ProcessFlagFile(ChangesDir);
			if (fileFlag.Lines.Count == 0) // means: we just created the file
				fileFlag.Update();
			return fileFlag;
		}
		private ProcessFlagFile fileFlag = null;

		public MasterFlagFile MasterFlag()
		{
			masterFlag = new MasterFlagFile(ChangesDir, "Master");   // read it or create it
			if (string.IsNullOrEmpty(masterFlag.Originator))    // means: we just created the file master.flag
				masterFlag.Update();    // takes ownership to this machine if no server set yet
			return masterFlag;
		}
		private MasterFlagFile masterFlag = null;
		#endregion

		#region store and load options (to be set via constructor)
		public bool ReadOnly { get; set; }
		public bool Backup { get; set; }
		#endregion

		/// <summary>
		/// Did the master update the file since I last used it?
		/// </summary>
		/// <returns>true if a reload seems necessary, false otherwise</returns>
		public bool MasterUpdatesAvailable()
		{
			return MasterFlag().Time.UtcTicks > ProcessFlag().Time.UtcTicks;
		}

		/// <summary>
		/// overload this in derived classes to give it some per JsonItem meaning
		/// </summary>
		/// <returns>timestamp</returns>
		public virtual DateTimeOffset GetFileChanged()
		{
			// TODO: Rainerquest — consider adding LastWriteTimeUtc to RaiFile so we don't need System.IO.FileInfo here
			var info = new System.IO.FileInfo(JsonFile.FullName);
			return info.LastWriteTimeUtc;
		}

		/// <summary>
		/// overload this in derived classes to give it some per JsonItem meaning once Infos is defined
		/// </summary>
		/// <returns>timestamp</returns>
		public virtual DateTimeOffset GetMemChanged()
		{
			return DateTimeOffset.UtcNow;   // means: memory is always newer
		}

		/// <summary>
		/// Checks whether the disk version of this data has newer changes than the in-memory version.
		/// </summary>
		/// <remarks>greater means younger</remarks>
		/// <returns>true, if the youngest setting on disk is younger than the youngest setting in memory</returns>
		public bool DiskHasNewerChanges()
		{
			if (!JsonFile.Exists())
				return false;
			return GetFileChanged() > GetMemChanged();
		}

		/// <summary>
		/// Changes from other servers are available when change files are there
		/// </summary>
		/// <returns>true if a reload seems necessary, false otherwise</returns>
		public bool ForeignChangesAvailable()
		{
			return EnumerateChangeFiles()
				.Any(changeFile => !changeFile.Name.EndsWith("_" + Environment.MachineName, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>
		/// Directory for change files and flag files, sitting alongside the pit file.
		/// Created on first access if it doesn't exist yet.
		/// </summary>
		public RaiPath ChangesDir
		{
			get
			{
				if (changesDir == null)
				{
					var dir = JsonFile.Path / "Changes";            // TODO: CR make RaiFile.Path of type RaiPath
					changesDir = dir.mkdir();
				}
				return changesDir;
			}
		}
		private RaiPath changesDir = null;

		protected IEnumerable<TextFile> EnumerateChangeFiles()
		{
			if (!ChangesDir.Exists())
				return Enumerable.Empty<TextFile>();

			return ChangesDir.EnumerateFiles("*.json", recursive: true)	// TODO: reconsider this - maybe false is ok
				.Select(file => new TextFile(file.FullName));
		}

		/// <summary>
		/// The JsonFile is the main file for the JsonPit
		/// </summary>
		/// <remarks>JsonFile is a directory and a file by the same name</remarks>
		public PitFile JsonFile
		{
			get { return jsonFile; }
			set { jsonFile = value; }
		}
		private PitFile jsonFile;

		protected bool descending;

		public JsonPitBase(bool readOnly = true, bool backup = false, bool unflagged = false, bool descending = false)
		{
			ReadOnly = readOnly;
			Backup = backup;
			this.unflagged = unflagged;
			this.descending = descending;
		}
	}

	/// <summary>
	/// Value with an attached timestamp and round-trip string format.
	/// </summary>
	public class TimestampedValue
	{
		/// <summary>
		/// Time: get may be deferred, set instantly
		/// </summary>
		public DateTimeOffset Time { get; set; }
		public string Value { get; set; }
		public override string ToString() => $"{Value}|{Time.UtcDateTime.ToString("o")}";

		public TimestampedValue(object value, DateTimeOffset? time = null)
		{
			Value = value == null ? string.Empty : value.ToString();
			Time = time ?? DateTimeOffset.UtcNow;
		}

		public TimestampedValue(string valueAndTime)
		{
			if (string.IsNullOrEmpty(valueAndTime))
			{
				Value = "";
				Time = DateTimeOffset.UtcNow;
			}
			else
			{
				var vtArray = valueAndTime.Split(new char[] { '|' });
				if (vtArray.Length == 2)
					Time = vtArray[1].Length == 0 ?
						DateTimeOffset.MinValue :
						DateTimeOffset.ParseExact(vtArray[1], "o", CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
				else Time = DateTimeOffset.UtcNow;
				Value = vtArray[0];
			}
		}

		public TimestampedValue()
		{
		}
	}

	public class MasterFlagFile : TextFile
	{
		public int mv(MasterFlagFile src, bool replace = false, bool keepBackup = false) => mv((RaiFile)src, replace, keepBackup);
		private readonly object locker = new object();

		public new void Save(bool backup = false)
		{
			lock (locker)
			{
				base.Save(backup);
			}
		}

		public string Originator
		{
			get
			{
				if (Lines == null || Lines.Count == 0)
					Read();
				return new TimestampedValue(Lines.Count == 0 ? "|" : Lines[0]).Value;
			}
			set
			{
				Lines = new List<string> { (new TimestampedValue(value)).ToString() };
				Save();
			}
		}

		public DateTimeOffset Time
		{
			get
			{
				Read();
				return (new TimestampedValue(Lines.Count == 0 ? "|" : Lines[0])).Time;
			}
			set
			{
				if (this.Lines == null || this.Lines.Count == 0)
					Read();
				var tv = new TimestampedValue(this.Lines.Count == 0 ? "|" : this.Lines[0])
				{
					Time = value
				};
				Lines = new List<string> { tv.ToString() };
				Save();
			}
		}

		public TimestampedValue Update(DateTimeOffset? time = null, string originator = null)
		{
			var tv = new TimestampedValue(Originator, DateTimeOffset.UtcNow);
			if (string.IsNullOrEmpty(tv.Value))
			{
				tv.Value = Environment.MachineName;
			}
			if (!string.IsNullOrEmpty(originator))
			{
				tv.Value = originator;
			}
			if (time != null)
			{
				tv.Time = (DateTimeOffset)time;
			}
			Lines = new List<string> { tv.ToString() };
			Save();
			return tv;
		}

		public static string FileName(string changeDir, string name) => changeDir + new RaiFile(name).Name + ".flag";

		public MasterFlagFile(RaiPath changeDir, string name, string server = null)
			: base(changeDir, name, ext: "flag")
		{
			if (!string.IsNullOrEmpty(server))
				Update(originator: server);
		}
	}

	public class ProcessFlagFile : MasterFlagFile
	{
		public int mv(ProcessFlagFile src, bool replace = false, bool keepBackup = false) => mv((RaiFile)src, replace, keepBackup);

		public static string CurrentProcessId()
		{
			var p = System.Diagnostics.Process.GetCurrentProcess();
			return $"{p.ProcessName}:{p.Id}";
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
				Lines = new List<string> { (new TimestampedValue(value)).ToString() };
				Save();
			}
		}

		public new TimestampedValue Update(DateTimeOffset? time = null, string process = null)
		{
			var tv = new TimestampedValue(CurrentProcessId(), DateTimeOffset.UtcNow)
			{
				Value = process == null ? CurrentProcessId() : process
			};
			if (time != null)
				tv.Time = (DateTimeOffset)time;
			Lines = new List<string> { tv.ToString() };
			Save();
			return tv;
		}

		public ProcessFlagFile(RaiPath changeDir)
			: base(changeDir, Environment.MachineName)
		{
		}
	}

	public enum Compare { JSON, ByProperty };

	/// <summary>
	/// JSON-backed item with metadata and change tracking.
	/// </summary>
	public class PitItem : JObject, IEquatable<PitItem>
	{
		public string Id
		{
			get { return (string)(this[nameof(Id)]); }
			set { this[nameof(Id)] = value; }
		}

		public DateTimeOffset Modified
		{
			get
			{
				return (DateTimeOffset)this[nameof(Modified)];
			}
			internal set
			{
				this[nameof(Modified)] = value.ToUniversalTime();
			}
		}

		public bool Deleted
		{
			get
			{
				var q = from _ in Properties() where _.Name == "Deleted" select _;
				if (q.Count() == 0)
					throw new KeyNotFoundException($"Deleted does not exist in Item {Id}");
				return (bool)this[nameof(Deleted)];
			}
			set { this[nameof(Deleted)] = value; }
		}

		public string Note
		{
			get { return (string)this[nameof(Note)]; }
			set { this[nameof(Note)] = value; }
		}

		public bool SetProperty(string objectAsJsonString)
		{
			return ExtendWith(JObject.Parse(objectAsJsonString));
		}

		public void SetProperty(object obj)
		{
			SetProperty(JSON.SerializeDynamic(obj));
		}

		public void DeleteProperty(string propertyName)
		{
			Deleted = false;
			Invalidate();
			this[propertyName] = null;
		}

		public bool Delete(string by = null, bool backDate100 = true)
		{
			if (Deleted)
				return false;
			Deleted = true;
			if (backDate100)
				Modified = DateTimeOffset.UtcNow - new TimeSpan(0, 0, 0, 100);
			Invalidate();
			var s = $"[{Modified.ToUniversalTime().ToString("u")}] deleted";
			if (!string.IsNullOrEmpty(by))
				s += " by " + by;
			Note = s + ";\n" + Note;
			return true;
		}

		protected bool Dirty { get; set; }
		virtual public bool Valid() { return !Dirty; }
		virtual public void Validate() { Dirty = false; }
		virtual public void Invalidate()
		{
			Dirty = true;
			Modified = DateTimeOffset.UtcNow;
		}

		public override string ToString()
		{
			var jsonSerializerSettings = new JsonSerializerSettings() { DateTimeZoneHandling = DateTimeZoneHandling.Utc };
			return JsonConvert.SerializeObject(this, jsonSerializerSettings);
		}

		#region IEquatable<PitItem>
		/// <summary>
		/// Two PitItems are considered equal if they have the same Id and Modified timestamp
		/// and identical JSON content.
		/// </summary>
		public bool Equals(PitItem other)
		{
			if (other == null)
				return false;
			if (Id != other.Id || Modified.UtcTicks != other.Modified.UtcTicks)
				return false;
			return ToString() == other.ToString();
		}

		public override bool Equals(object obj)
		{
			if (obj is PitItem other)
				return Equals(other);
			return base.Equals(obj);
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(Id, Modified.UtcTicks);
		}
		#endregion

		public bool Extend(string json)
		{
			var token = JToken.Parse(json);
			return token switch
			{
				JObject obj => ExtendWith(obj),
				JArray arr => ExtendWith(arr),
				_ => false
			};
		}

		public virtual bool ExtendWith(JObject obj)
		{
			var normalized = (JObject)obj.DeepClone();

			var originalClone = (JObject)this.DeepClone();
			var mergeSettings = new JsonMergeSettings
			{
				MergeArrayHandling = MergeArrayHandling.Replace,
				MergeNullValueHandling = MergeNullValueHandling.Ignore
			};

			this.Merge(normalized, mergeSettings);

			var changed = !JToken.DeepEquals(originalClone, this);
			if (changed)
			{
				Deleted = false;
				Invalidate();
			}

			return changed;
		}

		public virtual bool ExtendWith(JArray arr)
		{
			bool changed = false;
			foreach (var el in arr)
			{
				if (el is JObject row)
				{
					foreach (var attr in row)
					{
						if (!JToken.DeepEquals(this[attr.Key], attr.Value))
						{
							this[attr.Key] = attr.Value;
							changed = true;
						}
					}
				}
				else
				{
					if (!JToken.DeepEquals(this["_"], el))
					{
						this["_"] = el;
						changed = true;
					}
				}
			}
			if (changed)
			{
				Deleted = false;
				Invalidate();
			}
			return changed;
		}

		public PitItem(string id, bool invalidate = true, string comment = "")
		{
			this.Id = id;
			Note = comment;
			if (invalidate)
				Invalidate();
			Deleted = false;
		}

		public PitItem(string id, object extendWith, string comment = "")
			: this(id, JSON.SerializeDynamic(extendWith), comment)
		{
		}

		public PitItem(string id, string extendWithAsJson, string comment = "")
		{
			this.Id = id;
			Note = comment;
			Invalidate();
			Deleted = false;
			Extend(extendWithAsJson);
		}

		public PitItem(string id, bool invalidate, DateTimeOffset timestamp, string comment = "")
			: this(id, invalidate, comment)
		{
			Modified = timestamp;
		}

		public PitItem(PitItem other, DateTimeOffset? timestamp = null)
			: base(other)
		{
			Id = other.Id;
			Modified = timestamp == null ? (DateTimeOffset)other[nameof(Modified)] : (DateTimeOffset)timestamp;
		}

		public PitItem(JObject from)
			: base((JObject)from.DeepClone())
		{
			try
			{
				Deleted = (bool)this[nameof(Deleted)];
			}
			catch (Exception ex)
			{
				Deleted = false;
				if (Deleted) Console.WriteLine(ex);
			}
			Dirty = true;
			try
			{
				Modified = (DateTimeOffset)this[nameof(Modified)];
			}
			catch (Exception)
			{
				Modified = DateTimeOffset.UtcNow;
			}
			Id = (string)(this[nameof(Id)]);
			if (Property(nameof(Note)) != null)
				Note = (string)this[nameof(Note)];
		}

		public PitItem()
		{
		}
	}

	/// <summary>
	/// Timestamp comparison extensions for PitItem.
	/// </summary>
	public static class PitItemExtensions
	{
		static public long dtSharp = 0;

		static public bool isLike(this DateTimeOffset dto1, DateTimeOffset dto2)
		{
			return Math.Abs(dto1.UtcTicks - dto2.UtcTicks) <= dtSharp;
		}

		static public DateTimeOffset aligned(this DateTimeOffset dto1, DateTimeOffset dto2)
		{
			if (dto1.isLike(dto2))
				dto1 = dto2;
			return dto1;
		}
	}

	/// <summary>
	/// Equality comparer for PitItem using IEquatable implementation.
	/// </summary>
	class PitItemEqualityComparer : IEqualityComparer<PitItem>
	{
		public bool Equals(PitItem d1, PitItem d2)
		{
			if (d1 == null && d2 == null) return true;
			if (d1 == null || d2 == null) return false;
			return d1.Equals(d2);
		}

		public int GetHashCode(PitItem x) => x?.GetHashCode() ?? 0;
	}

	/// <summary>
	/// History stack of PitItem versions for a single key using immutable data structures for thread safety.
	/// </summary>
	public class PitItems : ItemsBase, IEnumerable<PitItem>
	{
		public ImmutableList<PitItem> History { get; private set; }
		public ImmutableList<PitItem> Items => History;
		public int MaxCount { get; init; } = 10;

		public PitItems(string key, ImmutableList<PitItem> history, int maxCount = 5) : base(key)
		{
			History = history ?? ImmutableList<PitItem>.Empty;
			MaxCount = maxCount;
		}

		public static PitItems Create(string key, int maxCount = 10) =>
			new PitItems(key, ImmutableList<PitItem>.Empty, maxCount);

		public PitItems Push(PitItem item)
		{
			var newHistory = History.Add(item)
				.Sort((a, b) => a.Modified.CompareTo(b.Modified));

			// MaxCount trimming
			if (MaxCount > 0 && newHistory.Count > MaxCount)
			{
				newHistory = newHistory.RemoveRange(0, newHistory.Count - MaxCount);
			}

			return new PitItems(Key, newHistory, MaxCount);
		}

		internal PitItem LatestFragment()
		{
			return History.IsEmpty ? null : History[^1];
		}

		private int FindProjectionStartIndex(DateTimeOffset? at)
		{
			if (History.IsEmpty)
				return -1;

			if (at == null)
				return History.Count - 1;

			for (int index = History.Count - 1; index >= 0; index--)
			{
				if (History[index].Modified <= at.Value)
					return index;
			}

			return -1;
		}

		public PitItem ProjectState(DateTimeOffset? at = null, bool withDeleted = false)
		{
			var startIndex = FindProjectionStartIndex(at);
			if (startIndex < 0)
				return null;

			var newestFragment = History[startIndex];
			if (newestFragment.Deleted)
			{
				if (!withDeleted)
					return null;

				var deletedProjection = new JObject
				{
					[nameof(PitItem.Id)] = newestFragment.Id,
					[nameof(PitItem.Modified)] = newestFragment.Modified,
					[nameof(PitItem.Deleted)] = true
				};

				return new PitItem(deletedProjection);
			}

			var accumulator = new JObject();
			for (int index = startIndex; index >= 0; index--)
			{
				var fragment = History[index];
				if (fragment.Deleted)
					break;

				foreach (var property in fragment.Properties())
				{
					if (accumulator.Property(property.Name) == null)
						accumulator.Add(property.Name, property.Value.DeepClone());
				}
			}

			accumulator[nameof(PitItem.Id)] = newestFragment.Id;
			accumulator[nameof(PitItem.Modified)] = newestFragment.Modified;
			accumulator[nameof(PitItem.Deleted)] = false;

			return new PitItem(accumulator);
		}

		public PitItem Peek(DateTimeOffset? timestamp = null)
		{
			return ProjectState(timestamp);
		}

		public JObject Get(DateTimeOffset? timestamp = null)
		{
			return ProjectState(timestamp);
		}

		public int Count => History.Count;

		public IEnumerator<PitItem> GetEnumerator() => History.GetEnumerator();
		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

		// Compatibility constructors
		public PitItems(string key = null, IEnumerable<PitItem> value = null, int maxCount = 5) : base(key)
		{
			MaxCount = maxCount;
			Key = key;
			var list = ImmutableList<PitItem>.Empty;
			if (value != null)
			{
				foreach (var v in value) list = list.Add(v);

				if (list.Count > 1)
					list = list.Sort((a, b) => a.Modified.CompareTo(b.Modified));

				Key = key ?? list.FirstOrDefault()?.Id;
			}
			History = list;
		}
	}

	/// <summary>
	/// JsonPit file container with item history and persistence.
	/// Implements IDisposable to ensure changes are persisted on cleanup.
	/// </summary>
	public class Pit : JsonPitBase, IEnumerable<PitItems>, IDisposable
	{
		private Func<PitItem, string> orderBy;
		public int DefaultMaxCount { get; }
		private bool disposed = false;

		public override DateTimeOffset GetMemChanged() => GetLatestItemChanged();

		private StringComparer Comparer => ignoreCase ? StringComparer.InvariantCultureIgnoreCase : StringComparer.InvariantCulture;
		private StringComparison Comparison => ignoreCase ? StringComparison.InvariantCultureIgnoreCase : StringComparison.InvariantCulture;

		public void ConsiderCase()
		{
			if (ignoreCase)
			{
				ignoreCase = false;
				if (HistoricItems != null)
					HistoricItems = new ConcurrentDictionary<string, PitItems>(HistoricItems, StringComparer.InvariantCulture);
			}
		}

		public void IgnoreCase()
		{
			if (!ignoreCase)
			{
				ignoreCase = true;
				if (HistoricItems != null)
					HistoricItems = new ConcurrentDictionary<string, PitItems>(HistoricItems, StringComparer.InvariantCultureIgnoreCase);
			}
		}

		private bool ignoreCase = false;

		// Replaced List implementation with ConcurrentDictionary containing Immutable PitItems
		public ConcurrentDictionary<string, PitItems> HistoricItems = new ConcurrentDictionary<string, PitItems>();
		public ICollection<string> Keys => HistoricItems.Keys;
		public bool ContainsKey(string key) => HistoricItems.ContainsKey(key);

		public bool Contains(string itemId, bool withDeleted = false)
		{
			var isThere = HistoricItems.Keys.Contains(itemId, Comparer);
			if (withDeleted)
				return isThere;
			if (!isThere)
				return false;
			var top = HistoricItems[itemId].ProjectState();
			return top != null && !top.Deleted;
		}

		public bool Invalid()
		{
			var query = from kvp in HistoricItems
						let latest = kvp.Value.LatestFragment()
						where latest != null && !latest.Valid()
						select latest.Id;
			return query.Any();
		}

		public DateTimeOffset GetLatestItemChanged()
		{
			var dates = from kvp in HistoricItems
						let latest = kvp.Value.LatestFragment()
						where latest != null
						select latest.Modified;
			return dates.Any() ? dates.Max() : DateTimeOffset.MinValue;
		}

		public PitItem this[string key]
		{
			get
			{
				if (!HistoricItems.TryGetValue(key, out var list))
					return default(PitItem);

				var top = list.ProjectState();
				if (top == null || top.Deleted)
					return default(PitItem);
				return top;
			}
		}

		public PitItem PitItem
		{
			set { Add(value); }
		}

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
				if (top != null && EqualsIgnoringModified(top, item))
					return false;

				var newStore = currentStore.Push(item);

				if (HistoricItems.TryUpdate(item.Id, newStore, currentStore))
				{
					return true;
				}
			}
		}

		public bool Add(string jsonObject)
		{
			var item = new PitItem(JObject.Parse(jsonObject));
			return Add(item);
		}

		public bool AddItems(IEnumerable<PitItem> items)
		{
			var result = true;
			foreach (var item in items)
			{
				result &= Add(item);
			}
			return result;
		}

		public bool AddItems(string jsonArray)
		{
			var jArray = JArray.Parse(jsonArray);
			var items = jArray.Select(jObj => new PitItem((JObject)jObj)).ToList();
			return AddItems(items);
		}

		private static JObject NormalizeIdentityPayload(object value)
		{
			JObject payload;
			if (value is JObject obj)
				payload = (JObject)obj.DeepClone();
			else
				payload = JObject.FromObject(value);

			return payload;
		}

		private static string GetIdentifier(JObject payload)
		{
			var itemId = (string)(payload["Id"]);
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
			if (string.IsNullOrEmpty(itemId))
				return true;
			try
			{
				var tombstone = new PitItem(itemId);
				if (tombstone.Delete(by, backDate))
					PitItem = tombstone; // Handles atomic updates via Add
			}
			catch (KeyNotFoundException) { }
			catch (Exception)
			{
				return false;
			}
			return true;
		}

		public bool RenameId(string oldKey, string newKey)
		{
			if (string.IsNullOrWhiteSpace(oldKey) || string.IsNullOrWhiteSpace(newKey))
				return false;
			if (string.Equals(oldKey, newKey, Comparison))
				return false;
			if (!Contains(oldKey))
				return false;
			if (Contains(newKey, withDeleted: true))
				return false;
			var oldItem = this[oldKey];
			if (oldItem == null)
				return false;
			var newItem = new PitItem(oldItem);
			newItem.SetProperty(new { Id = newKey });
			var oldItemDeleted = Delete(oldKey);
			var renamedAdded = Add(newItem);
			return oldItemDeleted && renamedAdded;
		}

		public JObject Get(string key, bool withDeleted = false)
		{
			if (!HistoricItems.TryGetValue(key, out var list))
				return default(PitItem);
			if (withDeleted)
				return list.ProjectState(withDeleted: true);
			return (JObject)this[key];
		}

		public PitItem GetAt(string key, DateTimeOffset timestamp, bool withDeleted = false)
		{
			if (!HistoricItems.TryGetValue(key, out var list))
				return default(PitItem);

			return list.ProjectState(timestamp, withDeleted);
		}

		public IEnumerable<KeyValuePair<DateTimeOffset, JToken>> ValuesOverTime(string oName, string pName)
		{
			if (!HistoricItems.TryGetValue(oName, out var list))
				return Enumerable.Empty<KeyValuePair<DateTimeOffset, JToken>>();

			var q = from item in list.History
					select new KeyValuePair<DateTimeOffset, JToken>(item.Modified, item.Deleted ? null : (JToken)item[pName]);
			return q;
		}

		public IEnumerable<KeyValuePair<DateTimeOffset, List<JToken>>> ValueListsOverTime(string oName, string pName)
		{
			if (!HistoricItems.ContainsKey(oName))
				return Enumerable.Empty<KeyValuePair<DateTimeOffset, List<JToken>>>();
			var values = ValuesOverTime(oName, pName);
			var q = (from kvp in values
					 select new KeyValuePair<DateTimeOffset, List<JToken>>(kvp.Key, (from _ in (JArray)kvp.Value select _).ToList()));
			return q;
		}

		public IEnumerable<JObject> AllUndeleted()
		{
			foreach (var key in Keys)
			{
				if (this[key] != null && !this[key].Deleted)
					yield return Get(key);
			}
		}

		public void ExportJson(RaiPath exportFilePath, DateTimeOffset? at = null, bool pretty = true)
		{
			var exportItems = new JArray();

			foreach (var key in Keys)
			{
				PitItem item;
				if (at == null)
					item = this[key];
				else
					item = GetAt(key, at.Value, withDeleted: false);

				if (item != null)
					exportItems.Add(item);
			}

			var formatting = pretty ? Formatting.Indented : Formatting.None;
			// Write directly to the file — no tmp-file-then-rename pattern in cloud-synced areas
			var exportFile = new TextFile(
				exportFilePath, JsonFile.Name, "json",
				content: exportItems.ToString(formatting));
		}

		public IEnumerable<dynamic> AllUndeletedDynamic()
		{
			return AllUndeleted().Select(jObj =>
			{
				dynamic expando = new ExpandoObject();
				var dict = (IDictionary<string, object>)expando;    // TODO: CR make RaiPath useable here

				foreach (var property in jObj.Properties())
				{
					dict[property.Name] = property.Value.Type == JTokenType.Object || property.Value.Type == JTokenType.Array
						? property.Value.ToObject<object>()
						: property.Value;
				}
				return expando;
			});
		}

		public string Subscriber { get; private set; }

		public void Load(bool undercover = false)
		{
			if (!JsonFile.Exists())
				return;
			var loadedOk = false;
			var hasData = false;
			try
			{
				var textFile = new TextFile(JsonFile.FullName);
				var jsonArrayOfArrayOfObject = string.Join(Environment.NewLine, textFile.Read());
				bool emptyFile = string.IsNullOrEmpty(jsonArrayOfArrayOfObject) || jsonArrayOfArrayOfObject.Length < 2;
				for (int i = 0, square = 0; i < jsonArrayOfArrayOfObject.Length && i < 100 && square < 2; i++)
				{
					if (jsonArrayOfArrayOfObject[i] == '[')
						square++;
					else if (jsonArrayOfArrayOfObject[i] == '{')
						throw new FormatException("JSON file format is not compatible with JsonPit");
				}

				HistoricItems = new ConcurrentDictionary<string, PitItems>(Comparer);
				if (!emptyFile)
					initValues(JArray.Parse(jsonArrayOfArrayOfObject));
				hasData = !emptyFile;
				loadedOk = true;
			}
			catch (InvalidOperationException)
			{
				throw;
			}
			finally
			{
				if (loadedOk && hasData && !(undercover || unflagged))
					ProcessFlag().Update(GetLatestItemChanged());
				Interlocked.Exchange(ref usingPersistence, 0);
			}
		}

		protected void Store(bool force = false, bool pretty = false, char indentChar = '\t')
		{
			if (HistoricItems == null)
				return;

			var jfExists = JsonFile.Exists();
			if (!jfExists && !HistoricItems.Any())
				return;

			if (!jfExists || force || Invalid())
			{
				if (ReadOnly)
					throw new System.IO.IOException("JsonFile " + JsonFile.Name + " was set to readonly mode but an attempt was made to execute JsonFile.Store");

				JsonFile.mkdir();

				// Write directly to the file — never use tmp-file-then-rename in cloud-synced areas.
				// The file must remain visible to the cloud provider at all times.
				// The master/client philosophy prevents simultaneous writes from multiple machines.
				var serializer = new JsonSerializer
				{
					DateFormatHandling = DateFormatHandling.IsoDateFormat
				};
				var rawJson = JToken.FromObject(GetRawPersistenceModel(), serializer)
					.ToString(pretty ? Formatting.Indented : Formatting.None);

				var pitFile = new TextFile(JsonFile.FullName);
				pitFile.Lines = new List<string> { rawJson };
				pitFile.Changed = true;
				pitFile.Save();

				// TODO: Rainerquest — consider adding SetLastWriteTimeUtc to RaiFile
				var changeTime = GetLatestItemChanged();
				//rather not: keep the Now change time for the file to not confuse the CloudProvider + User
				//System.IO.File.SetLastWriteTimeUtc(JsonFile.FullName, changeTime.UtcDateTime);

				if (!unflagged)
				{
					MasterFlag().Update(changeTime);
					ProcessFlag().Update(changeTime);
				}

				foreach (var kvp in HistoricItems)
				{
					var latest = kvp.Value.LatestFragment();
					latest?.Validate();
				}
			}
		}

		private IReadOnlyList<IReadOnlyList<PitItem>> GetRawPersistenceModel()
		{
			return HistoricItems
				.OrderBy(kvp => kvp.Key, Comparer)
				.Select(kvp => (IReadOnlyList<PitItem>)kvp.Value.History)
				.ToList();
		}

		public void Save(bool? backup = null, bool force = false)
		{
			if (backup != null)
				Backup = (bool)backup;
			if (ReadOnly)
				throw new System.IO.IOException("JsonFile " + JsonFile.Name + " was set to readonly mode but an attempt was made to execute JsonFile.Save");
			Monitor.Enter(_locker);
			try
			{
				if (RunningOnMaster())
					Store(force);
				else
					CreateChangeFiles();
			}
			finally
			{
				Monitor.Exit(_locker);
			}
		}

		/// <summary>
		/// Find changes in memory compared to the main pit file and persist them as ChangeFiles (one per changed fragment).
		/// A ChangeFile contains a single PitItem wrapped in the pit JSON format (array of arrays).
		/// </summary>
		private void CreateChangeFiles()
		{
			// Read the current state from disk (read-only — will pick up existing
			// ChangeFiles into memory but won't delete them; only the master
			// process consumes and removes ChangeFiles)
			var compareFile = new Pit(
				JsonFile.Path,
				undercover: true,
				unflagged: true,
				readOnly: true //! we will not write this instance back to disk
			);

			// Find what we have in memory that the disk version doesn't
			var myLocalChanges = CompareToOtherHistory(compareFile.HistoricItems);

			if (myLocalChanges.Count == 0)
				return;

			// Already under _locker from Save() — merge directly
			HistoricItems = compareFile.HistoricItems;

			foreach (var changedPitItems in myLocalChanges)
			{
				HistoricItems.AddOrUpdate(
					changedPitItems.Key,
					changedPitItems,
					(key, existingFromDisk) =>
					{
						var merged = existingFromDisk;
						foreach (var fragment in changedPitItems)
							merged = merged.Push(fragment);
						return merged;
					}
				);
			}

			// Write each changed PitItem as an individual ChangeFile
			foreach (var changedPitItems in myLocalChanges)
			{
				foreach (var fragment in changedPitItems)
					CreateChangeFile(fragment);
			}
		}

		/// <summary>
		/// Writes a single PitItem as a ChangeFile to the Changes subdirectory.
		/// The file uses pit format (array of arrays) so MergeChanges can read it as a Pit.
		/// Filename format: {ticks}_{machineName}.json
		/// </summary>
		public void CreateChangeFile(PitItem item, string server = null)
		{
			if (item == null)
				return;

			var ticks = item.Modified.UtcTicks.ToString();
			var machineName = server ?? Environment.MachineName;
			var fileName = $"{ticks}_{machineName}";

			var changeFile = new RaiFile(ChangesDir, fileName, "json");
			//changeFile.Ext = "json";

			if (changeFile.Exists())
				return;

			// Wrap in pit format: array of arrays, each inner array is one PitItems history
			var json = new JArray(new JArray(item.DeepClone())).ToString(Formatting.None);

			var textFile = new TextFile(changeFile.FullName);
			textFile.Lines = new List<string> { json };
			textFile.Changed = true;
			textFile.Save();
		}

		/// <summary>
		/// Compares this.HistoricItems with another set of historic items and returns the differences.
		/// Returns all PitItem fragments that exist in this.HistoricItems but are missing from the provided historicItems.
		/// </summary>
		/// <param name="historicItems">The other history to compare against (typically from disk)</param>
		/// <returns>A list of PitItems, each containing only the fragments missing from the other side.</returns>
		private List<PitItems> CompareToOtherHistory(ConcurrentDictionary<string, PitItems> historicItems)
		{
			var differences = new List<PitItems>();
			foreach (var kvp in HistoricItems)
			{
				if (!historicItems.TryGetValue(kvp.Key, out var otherItems))
				{
					// Case a: entire PitItems missing from the other side
					differences.Add(kvp.Value);
					continue;
				}
				// Case b: find individual PitItem fragments missing from the other side.
				// Build a set of (Id, Modified) from the other side for fast lookup.
				var otherKeys = new HashSet<(string Id, DateTimeOffset Modified)>(
					otherItems.Select(item => (item.Id, item.Modified))
				);
				var missingItems = kvp.Value
					.Where(item => !otherKeys.Contains((item.Id, item.Modified)))
					.ToList();
				if (missingItems.Count > 0)
				{
					differences.Add(new PitItems(kvp.Key, missingItems, DefaultMaxCount));
				}
			}
			return differences;
		}

		///TODO: 
		/// MergeChange differs from running on Master to running on Client only in 5. and 6.:
		/// 1. read the pit from disk first, to get what's current - it could have changes since the last load.
		/// 2. create a diff between the current in-memory history and the disk version, to find out what has changed in memory compared to disk
		/// 3. then make the History in Memory the same as the disk version and merge the result of the diff into the History in Memory
		/// 4. then read all change files into a memory structure (same as the Diff result) and merge that memory structure into the History inside the Pit in Memory
		/// 5. then delete all change files that were read and processed.
		/// 6. then store the merged result back to disk as the new main pit file, which will be the source of truth for all clients until the next change is made and propagated.
		/// => 5. and 6. are only executed by the Master, to avoid multiple machines trying to delete the same change files and writing the same main file at the same time.
		/// It would probably be wise to check if the current process has Master rights just after having finisherd 4. and before starting 5.
		/// 
		/// We have to implement the fetching of the Masterflag in a sophisticated and seperate thread or process asynchronously.
		/// My best guess is that we stay out of trouble if we do the following:
		/// Every machine/Process combination writes a ProcessFlag file with it's own timestamp every time it makes a change, so we can track when each machine last made 
		/// a change by just looking at the CloudDrive. Nkosikazi-pits.flag and Nkosikazi-AfricaStage.flag need to be seperate files.
		/// We are still in the folder of the Pit, which is telling everybody that those flag files are for accessing that very pit.
		/// If say pits reads the Pit it writes its timestamp in its Nkosikazi-pits.flag file after reading. Let's say it read as client because the
		/// ticket in the Master.flag file was not expired yet (expiration time 1min for now). 
		/// Nkosikazi-RAIkeep.flag was also active around that time and had the Master, which is documented by a timed ticket inside Master.flag.
		/// If for whatever reason, more than one copy of Master*.flag exists in that directory than this shows that the Cloud Provider has created a conflicted copy.
		/// We have to think through, who and when is allowed to delete those conflicted copies. We can let them hang there for now and only manually delete.
		/// Whoever has the Master rights writes their timed ticket into the Master.flag file and the name of Machine-Process in front of it (seperated by |). They also 
		/// write their Process flag file (which is undisputed anyway).
		/// When checking if I am master, a process checks
		/// a) is there anybody out there (collecting up all the flag files, readonly mode). Checking: did anybody do anything within the last 60s?
		/// b) is the ticket in the Master.flag expired? Means: has it been 60s since the last ticket entry was created OR is it me who has Master status? (careful)
		/// c) if nobody around was active and the ticket has expired, (or maybe for some other reasons when I am already master), I write a new ticket
		/// 		in the masterfile that is good for another 60s.
		/// d) if I was able to odo that, I continue to 5. and 6. in MergeChanges, otherwise I don't.
		/// Q: is there a danger that I keep reaching 4 and never 5? Or anybody else also never reaches 5?
		public void MergeChanges()
		{
			if (ChangesDir.Exists())
			{
				foreach (var file in EnumerateChangeFiles().OrderByDescending(x => x))
				{
					try
					{
						var changePit = new Pit(file.Path, undercover: true);

						foreach (var changeItems in changePit)
						{
							MergeIntoHistory(changeItems);
						}

						// Only the master deletes change files, and only after they've had time to propagate
						if (RunningOnMaster() && !ReadOnly)
						{
							var rf = new RaiFile(changePit.JsonFile.FullName);
							if (rf.FileAge.TotalSeconds > 600)
							{
								try
								{
									rf.rm();
								}
								catch (Exception)
								{
								}
							}
						}
					}
					catch (InvalidOperationException)
					{
					}
				}
			}
			if (!ReadOnly)
				Store();
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

		public bool Reload()
		{
			var masterUpdates = MasterUpdatesAvailable();
			var foreignChanges = ForeignChangesAvailable();

			if (masterUpdates && RunningOnMaster())
			{
				throw new Exception($"Some process changed the main file without permission => inconsistent data in {nameof(Reload)}, file {JsonFile.Name}");
			}
			if (masterUpdates)
			{
				Save();
				Load();
				return true;
			}
			if (!masterUpdates && foreignChanges)
			{
				MergeChanges();
				return true;
			}
			if (Invalid())
			{
				Save();
				return true;
			}
			return false;
		}

		IEnumerator IEnumerable.GetEnumerator()
		{
			foreach (var item in HistoricItems)
				yield return item.Value;
		}

		public IEnumerator<PitItems> GetEnumerator()
		{
			foreach (var kvp in HistoricItems)
				yield return kvp.Value;
		}

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

						var q = from o in inner.OfType<JObject>() select new PitItem(o);
						if (!q.Any())
							break;

						var stack = PitItems.Create(q.Last().Id, DefaultMaxCount);
						foreach (var item in q)
							stack = stack.Push(item);

						HistoricItems.TryAdd(q.Last().Id, stack);
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
			if (values != null && values.Any())
			{
				foreach (var pitItems in values)
				{
					if (pitItems.Count > 0)
					{
						var q = from o in pitItems select new PitItem(o);
						var stack = PitItems.Create(q.Last().Id, DefaultMaxCount);
						foreach (var item in q) stack = stack.Push(item);
						HistoricItems.TryAdd(q.Last().Id, stack);
					}
				}
			}
		}
		// [Obsolete("orderBy not available anymore as constructor parameter because the order is intrinsically determined by the PitItems stack and cannot be given as an argument; use any other constructor with default odering")]
		// public Pit(string pitDirectory, IEnumerable<PitItems> values = null, string subscriber = null, Func<PitItem, string> orderBy = null, bool descending = false,
		// 						bool readOnly = true, bool backup = false, bool undercover = false, bool unflagged = false, bool autoload = true, bool ignoreCase = false, string version = "")
		// { }
		public Pit(RaiPath pitDirectory, IEnumerable<PitItems> values = null, string subscriber = null,
							bool descending = false, bool readOnly = true, bool backup = false, bool undercover = false,
							bool unflagged = false, bool autoload = true, bool ignoreCase = false, string version = "")
			: base(readOnly, backup, unflagged, descending)
		{
			if (pitDirectory == null || pitDirectory.ToString().Length < 3)
				throw new ArgumentException("pitDirectory must be a valid PitDirectory");
			string[] segments = pitDirectory.ToString().Split(Os.DIR, StringSplitOptions.RemoveEmptyEntries);
			if (segments.Length == 0)
				throw new ArgumentException("pitDirectory must contain at least one valid segment");
			JsonFile = new PitFile(pitDirectory, name: segments[^1]);
			Subscriber = subscriber;
			this.orderBy = orderBy ?? new Func<PitItem, string>(x => x.Id);
			this.descending = descending;
			HistoricItems = new ConcurrentDictionary<string, PitItems>();
			initValues(values);
			if (autoload)
			{
				if (JsonFile.Exists())
					Load(undercover);
				MergeChanges();
			}
			if (string.IsNullOrEmpty(JsonFile.Name) || string.IsNullOrEmpty(JsonFile.Ext))
				throw new ArgumentException("JsonFile must have a valid name and extension - 3");
		}
		// [Obsolete("orderBy not available anymore as constructor parameter because the order is intrinsically determined by the PitItems stack and cannot be given as an argument; use any other constructor with default odering")]
		// public Pit(JArray values, string pitDirectory, string subscriber = null, Func<PitItem, string> orderBy = null, bool descending = false,
		// 				bool readOnly = true, bool backup = false, bool undercover = false, bool unflagged = false, bool autoload = true, bool ignoreCase = false, string version = "")
		// { }

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
		/// Constructor for opening a Pit from a PitFile
		/// </summary>
		/// <param name="pitFile">autoloads the values from pitFile if any (autoload == true)</param>
		/// <param name="readOnly">Intention to Add PitItems? => false</param>
		public Pit(PitFile pitFile, bool readOnly = false)
			: base(readOnly, backup: false, unflagged: false, descending: false)
		{
			if (pitFile == null)
				throw new ArgumentNullException(nameof(pitFile));

			JsonFile = pitFile;
			Subscriber = null;
			this.orderBy = x => x.Id;
			this.descending = false;
			HistoricItems = new ConcurrentDictionary<string, PitItems>();

			if (JsonFile.Exists())
				Load(undercover: false);
			MergeChanges();

			if (string.IsNullOrEmpty(JsonFile.Name) || string.IsNullOrEmpty(JsonFile.Ext))
				throw new ArgumentException("JsonFile must have a valid name and extension - 1");
		}

		#region IDisposable
		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (!disposed)
			{
				if (disposing && !ReadOnly)
				{
					Save(backup: true, force: false);
					Debug.WriteLine($"{JsonFile.Name} saved to {JsonFile.Path}");
				}
				disposed = true;
			}
		}

		~Pit()
		{
			Dispose(false);
		}
		#endregion
	}

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
		virtual public DateTimeOffset Changed()
		{
			return Modified;
		}
		public bool Deleted { get; set; }
		public bool Delete(string by = null, bool backDate100 = true)
		{
			if (!Deleted)
			{
				Deleted = true;
				if (backDate100)
					Modified = DateTimeOffset.UtcNow - new TimeSpan(0, 0, 0, 100);
				Invalidate();
				var s = $"[{Modified.ToUniversalTime().ToString("u")}] deleted";
				if (!string.IsNullOrEmpty(by))
					s += " by " + by;
				Note = s + ";\n" + Note;
			}
			return true;
		}
		protected bool Dirty { get; set; }
		virtual public bool Valid() { return !Dirty; }
		virtual public void Validate() { Dirty = false; }
		virtual public void Invalidate()
		{
			Dirty = true;
			Modified = DateTimeOffset.UtcNow;
		}
		public string Note { get; set; }
		public override string ToString()
		{
			return JSON.Serialize<Item>(this);
		}
		public virtual bool Matches(Item x)
		{
			return x.Id == Id;
		}
		public virtual bool Matches(SearchExpression se)
		{
			return se.IsMatch(this);
		}
		public virtual bool Matches(string filter, Compare comp = Compare.ByProperty)
		{
			if (comp == Compare.JSON)
			{
				if (string.IsNullOrWhiteSpace(filter))
					return true;
				var filterStrings = filter.Split(new char[] { '+', ' ' });
				var json = ToString();
				foreach (string filterString in filterStrings)
					if (!json.Contains(filterString))
						return false;
				return true;
			}
			var se = new SearchExpression(filter);
			return se.IsMatch(this);
		}
		public T Clone<T>()
		{
			var s = JSON.SerializeDynamic(this, JsonPitBase.jilOptions);
			return JSON.Deserialize<T>(s, JsonPitBase.jilOptions);
		}
		public virtual dynamic Clone()
		{
			var s = JSON.SerializeDynamic(this, JsonPitBase.jilOptions);
			dynamic o;
			if (this.GetType().FullName.Contains("Dynamic"))
				o = JSON.DeserializeDynamic(s, JsonPitBase.jilOptions);
			else
			{
				var jsonSerializerSettings = new JsonSerializerSettings()
				{
					DateFormatHandling = DateFormatHandling.IsoDateFormat,
					DateParseHandling = DateParseHandling.DateTimeOffset,
					DateTimeZoneHandling = DateTimeZoneHandling.Utc
				};
				o = JsonConvert.DeserializeObject(s, jsonSerializerSettings);
			}
			return o;
		}
		public virtual void Merge(Item second)
		{
			if (Id != second.Id)
				throw new ArgumentException("Error: " + Id + ".Merge(" + second.Id + ") is an invalid call - Ids must be equal.");
			if (Changed().UtcTicks == second.Changed().UtcTicks)
			{
				Dirty = false;
				return;
			}
			if (Changed().UtcTicks <= second.Changed().UtcTicks)
			{
				Dirty = true;
				Modified = second.Modified;
				if (second.Deleted)
				{
					Dirty = Dirty || Deleted != second.Deleted;
					Deleted = true;
				}
				else Deleted = false;
				foreach (var propertyInfo in this.GetType().GetProperties())
				{
					if (propertyInfo.CanWrite)
					{
						dynamic value;
						try
						{
							value = propertyInfo.GetValue(second, null);
							propertyInfo.SetValue(this, value, null);
						}
						catch (System.Reflection.TargetParameterCountException)
						{
							try
							{
								value = propertyInfo.GetValue(this, null);
								propertyInfo.SetValue(this, value, null);
							}
							catch (System.Reflection.TargetParameterCountException)
							{
							}
						}
					}
				}
			}
			else Dirty = true;
		}
		public Item(string id, string comment, bool invalidate = true)
		{
			this.Id = id;
			Note = comment;
			if (invalidate)
				Invalidate();
		}
		public Item(Item from)
		{
			var clone = from.Clone();
			foreach (var propertyInfo in this.GetType().GetProperties())
				if (propertyInfo.CanWrite)
					propertyInfo.SetValue(this, propertyInfo.GetValue(clone, null), null);
			Modified = from.Changed();
		}
		public Item()
		{
		}
	}
}
