using Jil;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using RaiUtils;
using OsLib;
using System.Dynamic;
using System.Collections.Immutable;

namespace JsonPit
{
	/// <summary>
	/// Base container holding a key identifier for item groups.
	/// </summary>
	public class ItemsBase
	{
		/// <summary>Identifying name, i.e. JsonFileName from enclosing JsonFile</summary>
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
		public static string ConfigDirDefault
		{
			get
			{
				if (configDirDefault == null)
					configDirDefault = new RaiPath($"{Os.CloudStorageRoot}Config").Path;
				return configDirDefault;
			}
			set
			{
				configDirDefault = value;
			}
		}
		private static string configDirDefault = null;
		public static Options jilOptions = new Options(prettyPrint: true, excludeNulls: false, jsonp: false, dateFormat: DateTimeFormat.ISO8601, includeInherited: true);
		#region Semaphore
		protected static int usingPersistence = 0;                // used by Interlocked
		protected static readonly object _locker = new object();  // used by Monitor ... the question is: why do I need both?
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
				fileFlag = new ProcessFlagFile(ChangeDir);
			if (fileFlag.Lines.Count == 0) // means: we just created the file
				fileFlag.Update();
			return fileFlag;
		}
		private ProcessFlagFile fileFlag = null;
		public MasterFlagFile MasterFlag()
		{
			masterFlag = new MasterFlagFile(ChangeDir, "Master");   // read it or create it
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
			return MasterFlag().Time.UtcTicks > ProcessFlag().Time.UtcTicks;    // maybe, everything within 10 ticks should is considered the same time???
		}
		/// <summary>
		/// overload this in derived classes to give it some per JsonItem meaning
		/// </summary>
		/// <returns>timestamp</returns>
		public virtual DateTimeOffset GetFileChanged()
		{
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
		/// Loads the file from disk to compare the JsonItem.modified attribute of all JsonItem stored in it
		/// </summary>
		/// <remarks>greater means younger</remarks>
		/// <returns>true, if the youngest setting on disk is younger than the youngest setting in memory</returns>
		public bool FileHasChangedOnDisk()
		{
			if (!File.Exists(JsonFile.FullName))
				return false;
			return GetFileChanged() > GetMemChanged();
		}
		/// <summary>
		/// Changes from other servers are available when change files are there
		/// </summary>
		/// <returns>true if a reload seems necessary, false otherwise</returns>
		public bool ForeignChangesAvailable()
		{
			return (
				from _ in Directory.GetFiles(ChangeDir, "*.json")
				where !(_).EndsWith("_" + Environment.MachineName + ".json")
				select _
			).Count() > 0;
		}
		/// <summary>
		/// Directory for change files
		/// </summary>
		/// <remarks>The main pit file is now within a folder by the same name and Change files are in a subfolder called Changes</remarks>
		public string ChangeDir
		{
			get
			{
				var file = new RaiFile(JsonFile.Path + JsonFile.Name + Os.DIRSEPERATOR + "Changes" + Os.DIRSEPERATOR);
				file.mkdir();
				return file.Path;
			}
		}
		/// <summary>
		/// The JsonFile is the main file for the JsonPit
		/// </summary>
		/// <remarks>JsonFile is a directory and a file by the same name</remarks>
		public PitFile JsonFile
		{
			get { return jsonFile; }
			set
			{
				jsonFile = value;
			}
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
		private static object Locker = new object();
		public new void Save(bool backup = false)
		{
			Monitor.Enter(Locker);
			try
			{
				base.Save(backup);
			}
			finally
			{
				Monitor.Exit(Locker);
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
		public MasterFlagFile(string changeDir, string name, string server = null)
			: base(FileName(changeDir, name))
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
		public ProcessFlagFile(string changeDir)
			: base(changeDir, Environment.MachineName)
		{
		}
	}

	public enum Compare { JSON, ByProperty };

	/// <summary>
	/// JSON-backed item with metadata and change tracking.
	/// </summary>
	public class PitItem : JObject
	{
		public string Name
		{
			get { return (string)this[nameof(Name)]; }
			set { this[nameof(Name)] = value; }
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
					throw new KeyNotFoundException($"Deleted does not exist in Item {Name}");
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
			var obj = JObject.Parse(objectAsJsonString);
			bool changed = false;
			foreach (var kvp in obj)
			{
				var existing = this[kvp.Key];
				var incoming = kvp.Value;
				if (!JToken.DeepEquals(existing, incoming))
				{
					this[kvp.Key] = incoming;
					changed = true;
				}
			}
			if (changed)
			{
				Deleted = false;
				Invalidate();
			}
			return changed;
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
		public bool Extend(string json)
		{
			var token = JToken.Parse(json);
			bool changed = token switch
			{
				JObject obj => ExtendWith(obj),
				JArray arr => ExtendWith(arr),
				_ => false
			};
			if (changed)
			{
				Deleted = false;
				Invalidate();
			}
			return changed;
		}
		public virtual bool ExtendWith(JObject obj)
		{
			bool changed = false;
			foreach (var attr in obj)
			{
				if (!JToken.DeepEquals(this[attr.Key], attr.Value))
				{
					this[attr.Key] = attr.Value;
					changed = true;
				}
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
			return changed;
		}
		public PitItem(string name, bool invalidate = true, string comment = "")
		{
			this.Name = name;
			Note = comment;
			if (invalidate)
				Invalidate();
			Deleted = false;
		}
		public PitItem(string name, object extendWith, string comment = "")
			: this(name, JSON.SerializeDynamic(extendWith), comment)
		{
		}
		public PitItem(string name, string extendWithAsJson, string comment = "")
		{
			this.Name = name;
			Note = comment;
			Invalidate();
			Deleted = false;
			Extend(extendWithAsJson);
		}
		public PitItem(string name, bool invalidate, DateTimeOffset timestamp, string comment = "")
			: this(name, invalidate, comment)
		{
			Modified = timestamp;
		}
		public PitItem(PitItem other, DateTimeOffset? timestamp = null)
			: base(other)
		{
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
			Name = (string)this[nameof(Name)];
			Note = (string)this[nameof(Note)];
		}
		public PitItem()
		{
		}
	}

	public static class PitItemExtensions
	{
		static public bool Equals(this PitItem pi1, PitItem pi2)
		{
			if (pi2 == null && pi1 == null)
				return true;
			else if (pi1 == null | pi2 == null)
				return false;
			else if (pi1.Name == pi2.Name && pi1.Modified.isLike(pi2.Modified))
				return pi1.ToString() == pi2.ToString();
			return false;
		}
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

	class PitItemEqualityComparer : IEqualityComparer<PitItem>
	{
		public bool Equals(PitItem d1, PitItem d2) => JsonPit.PitItemExtensions.Equals(d1, d2);
		public int GetHashCode(PitItem x)
		{
			string s = $"{x.Name}{x.Modified.UtcTicks}";
			return s.GetHashCode();
		}
	}

	/// <summary>
	/// History stack of PitItem versions for a single key using immutable data structures for thread safety.
	/// </summary>
	public record PitItems(string Key, ImmutableList<PitItem> History, int MaxCount = 5) : ItemsBase(Key), IEnumerable<PitItem>
	{
		public static PitItems Create(string key, int maxCount = 5) => 
			new PitItems(key, ImmutableList<PitItem>.Empty, maxCount);

		public PitItems Push(PitItem item)
		{
			var newHistory = History.Add(item);

			// Keep order by Modified (ascending) so Peek() returns newest.
			if (newHistory.Count > 1 && newHistory[^2].Modified > item.Modified)
			{
				newHistory = newHistory.Sort((a, b) => a.Modified.CompareTo(b.Modified));
			}

			// MaxCount trimming
			if (MaxCount > 0 && newHistory.Count > MaxCount)
			{
				newHistory = newHistory.RemoveRange(0, newHistory.Count - MaxCount);
			}

			return this with { History = newHistory };
		}

		public PitItem Peek(DateTimeOffset? timestamp = null)
		{
			if (History.IsEmpty) 
				return null;

			if (timestamp == null) 
				return History.Last();

			for (int i = History.Count - 1; i >= 0; i--)
			{
				if (timestamp > History[i].Modified)
					return History[i];
			}
			return null;
		}

		public JObject Get(DateTimeOffset? timestamp = null)
		{
			return Peek(timestamp);
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
				foreach(var v in value) list = list.Add(v);
				
				if(list.Count > 1)
					list = list.Sort((a, b) => a.Modified.CompareTo(b.Modified));
					
				Key = key ?? list.FirstOrDefault()?.Name;
			}
			History = list;
		}
	}

	/// <summary>
	/// JsonPit file container with item history and persistence.
	/// </summary>
	public class Pit : JsonPitBase, IEnumerable<PitItems>
	{
		private Func<PitItem, string> orderBy;
		public int DefaultMaxCount { get; }

		public static string defaultPitName(string pit, string subscriber, string version = null)
		{
			if (version != null && version.Length == 0)
				version = Version;
			if (!pit.ToLower().Contains("_p"))
				pit += "_pit";
			var file = new RaiFile($"{pit}.json") { Path = ConfigDirDefault };
			if (!string.IsNullOrEmpty(version))
				file.Path += version + Os.DIRSEPERATOR;
			if (!string.IsNullOrWhiteSpace(subscriber))
				file.Path += subscriber + Os.DIRSEPERATOR;
			file.Path += file.Name.ToUpper()[0];
			return file.FullName;
		}

		public override DateTimeOffset GetMemChanged() => GetLastestItemChanged();

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
		
		public bool Contains(string itemName, bool withDeleted = false)
		{
			var isThere = HistoricItems.Keys.Contains(itemName, Comparer);
			if (withDeleted)
				return isThere;
			if (!isThere)
				return false;
			var top = HistoricItems[itemName].Peek();
			return top != null && !top.Deleted;
		}

		public bool Invalid()
		{
			var query = from kvp in HistoricItems where !kvp.Value.Peek().Valid() select kvp.Value.Peek().Name;
			return query.Count() > 0;
		}

		public DateTimeOffset GetLastestItemChanged()
		{
			var list = (from kvp in HistoricItems select kvp.Value.Peek().Modified);
			list = list.OrderByDescending(x => x);
			return list.Count() > 0 ? list.Last() : DateTimeOffset.MinValue;
		}

		public PitItem this[string key]
		{
			get
			{
				if (!HistoricItems.TryGetValue(key, out var list))
					return default(PitItem);
					
				var top = list.Peek();
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
				var pitItem = this[value.Name] ?? new PitItem(value.Name);
				if (pitItem.SetProperty(value))
					Add(pitItem);
			}
		}

		/// <summary>
		/// Add a PitItem as a new historic version using lock-free CAS algorithm.
		/// </summary>
		public bool Add(PitItem item)
		{
			while (true)
			{
				var currentStore = HistoricItems.GetOrAdd(item.Name, key => PitItems.Create(key, DefaultMaxCount));

				var top = currentStore.Peek();
				if (top != null && EqualsIgnoringModified(top, item))
					return false;

				var newStore = currentStore.Push(item);

				if (HistoricItems.TryUpdate(item.Name, newStore, currentStore))
				{
					return true;
				}
			}
		}

		private static bool EqualsIgnoringModified(PitItem a, PitItem b)
		{
			var ja = (JObject)a.DeepClone();
			var jb = (JObject)b.DeepClone();
			ja.Remove("Modified");
			jb.Remove("Modified");
			return JToken.DeepEquals(ja, jb);
		}

		public bool Delete(string itemName, string by = null, bool backDate = true)
		{
			if (string.IsNullOrEmpty(itemName))
				return true;
			try
			{
				if (!HistoricItems.TryGetValue(itemName, out var list))
					return true;
					
				var item = list.Peek();
				
				if (item == null)
					return true;
				if (item.Delete(by, backDate))
					PitItem = item; // Handles atomic updates via Add
			}
			catch (KeyNotFoundException) { }
			catch (Exception)
			{
				return false;
			}
			return true;
		}

		public JObject Get(string key, bool withDeleted = false)
		{
			if (!HistoricItems.TryGetValue(key, out var list))
				return default(PitItem);
			if (withDeleted)
				return list.Peek();
			return (JObject)this[key];
		}

		public PitItem GetAt(string key, DateTimeOffset timestamp, bool withDeleted = false)
		{
			if (!HistoricItems.TryGetValue(key, out var list))
				return default(PitItem);
				
			var item = list.Peek(timestamp);
			if (!withDeleted && item != null && item.Deleted)
				return default(PitItem);
			return item;
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

		public IEnumerable<dynamic> AllUndeletedDynamic()
		{
			return AllUndeleted().Select(jObj =>
			{
				dynamic expando = new ExpandoObject();
				var dict = (IDictionary<string, object>)expando;

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
				var jsonArrayOfArrayOfObject = File.ReadAllText(JsonFile.FullName);
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
					ProcessFlag().Update(GetLastestItemChanged());
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
					throw new IOException("JsonFile " + JsonFile.Name + " was set to readonly mode but an attempt was made to execute JsonFile.Store");

				JsonFile.mkdir();

				var tmpFile = new TmpFile(JsonFile.FullName, ext: "tmp");
				if (tmpFile.Exists())
					tmpFile.rm();

				using (FileStream fs = File.Open(tmpFile.FullName, FileMode.CreateNew))
				using (StreamWriter sw = new StreamWriter(fs))
				using (JsonTextWriter jw = new JsonTextWriter(sw))
				{
					jw.Formatting = pretty ? Formatting.Indented : Formatting.None;
					jw.IndentChar = indentChar;
					jw.Indentation = 1;

					var serializer = new JsonSerializer
					{
						DateFormatHandling = DateFormatHandling.IsoDateFormat
					};
					serializer.Serialize(jw, this);
				}

				JsonFile.mv(tmpFile, true, true);

				var changeTime = GetLastestItemChanged();
				File.SetLastWriteTimeUtc(JsonFile.FullName, changeTime.UtcDateTime);

				if (!unflagged)
				{
					MasterFlag().Update(changeTime);
					ProcessFlag().Update(changeTime);
				}

				foreach (var kvp in HistoricItems)
					kvp.Value.Peek().Validate();
			}
		}

		public void Save(bool? backup = null, bool force = false)
		{
			if (backup != null)
				Backup = (bool)backup;
			if (ReadOnly)
				throw new IOException("JsonFile " + JsonFile.Name + " was set to readonly mode but an attempt was made to execute JsonFile.Save");
			Monitor.Enter(_locker);
			try
			{
				if (RunningOnMaster())
					Store(force);
				else CreateChangeFiles();
			}
			finally
			{
				Monitor.Exit(_locker);
			}
		}

		private void CreateChangeFiles()
		{
			var compareFile = new Pit(JsonFile.FullName, undercover: true, unflagged: true);
			foreach (var name in Keys)
			{
				if (!HistoricItems.TryGetValue(name, out var list))
					continue;
				var latest = list.Peek();
				if (latest == null)
					continue;
				if (!compareFile.ContainsKey(name))
				{
					CreateChangeFile(latest);
					continue;
				}
				if (compareFile.HistoricItems.TryGetValue(name, out var compareList))
				{
					var compareLatest = compareList.Peek();
					if (compareLatest != null && latest.Modified > compareLatest.Modified)
						CreateChangeFile(latest);
				}
			}
		}

		public void CreateChangeFile(PitItem item, string machineName = null)
		{
			if (machineName == null)
				machineName = Environment.MachineName;
			var items = new JArray();
			var inner = new JArray();
			inner.Add(item);
			items.Add(inner);
			var changeFile = new RaiFile(ChangeDir + item.Modified.UtcTicks.ToString() + "_" + machineName + ".json");
			if (!File.Exists(changeFile.FullName))
				new Pit(items, changeFile.FullName, unflagged: true, readOnly: false).Save();
		}

		public void MergeChanges()
		{
			var changes = new PitItems();
			Pit changePit;
			if (Directory.Exists(ChangeDir))
			{
				foreach (var file in Directory.GetFiles(ChangeDir, "*.json").OrderByDescending(x => x))
				{
					try
					{
						changePit = new Pit(file, undercover: true);
						Pit pit = new Pit(pitDirectory: file);
						
						foreach (var changeItems in pit)
						{
							MergeIntoHistory(changeItems);
						}
						
						if (RunningOnMaster() && (DateTimeOffset.UtcNow - (new System.IO.FileInfo(file)).CreationTime).TotalSeconds > 600)
							if (!ReadOnly)
								new RaiFile(file).rm();
					}
					catch (System.InvalidOperationException)
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
			dynamic top;
			foreach (JArray inner in values)
			{
				if (inner.HasValues)
				{
					var q = from o in inner select new PitItem((JObject)o);
					var stack = PitItems.Create(q.Last().Name, DefaultMaxCount);
					foreach (var item in q) stack = stack.Push(item);
					
					top = (dynamic)inner.Last();
					HistoricItems.TryAdd((string)top.Name, stack);
				}
			}
		}

		private void initValues(IEnumerable<PitItems> values)
		{
			if (values != null && values.Count() > 0)
			{
				dynamic top;
				foreach (var pitItems in values)
				{
					if (pitItems.Count > 0)
					{
						var q = from o in pitItems select new PitItem(o);
						var stack = PitItems.Create(q.Last().Name, DefaultMaxCount);
						foreach (var item in q) stack = stack.Push(item);
						
						top = (dynamic)pitItems.Last();
						HistoricItems.TryAdd((string)top.Name, stack);
					}
				}
			}
		}

		public Pit(string pitDirectory, IEnumerable<PitItems> values = null, string subscriber = null, Func<PitItem, string> orderBy = null, bool descending = false,
						bool readOnly = true, bool backup = false, bool undercover = false, bool unflagged = false, bool autoload = true, bool ignoreCase = false, string version = "")
			: base(readOnly, backup, unflagged, descending)
		{
			JsonFile = new PitFile(pitDirectory);
			Subscriber = subscriber;
			this.orderBy = orderBy ?? new Func<PitItem, string>(x => x.Name);
			this.descending = descending;
			HistoricItems = new ConcurrentDictionary<string, PitItems>();
			initValues(values);
			if (autoload)
			{
				if (JsonFile.Exists())
					Load(undercover);
				MergeChanges();
			}
		}

		public Pit(JArray values, string pitDirectory, string subscriber = null, Func<PitItem, string> orderBy = null, bool descending = false,
				bool readOnly = true, bool backup = false, bool undercover = false, bool unflagged = false, bool autoload = true, bool ignoreCase = false, string version = "")
			: this(pitDirectory, Enumerable.Empty<PitItems>(), subscriber, orderBy, descending, readOnly, backup, undercover, unflagged, autoload, ignoreCase, version)
		{
			initValues(values);
		}

		~Pit()
		{
			if (!ReadOnly)
			{
				Save(backup: true, force: false);
				Debug.WriteLine($"{JsonFile.Name} saved to {JsonFile.Path}");
			}
		}
	}

	/// <summary>
	/// Base item with modified tracking and dirty state management.
	/// </summary>
	public class Item : ICloneable
	{
		public string Name { get; set; }
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
			return x.Name == Name;
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
			if (Name != second.Name)
				throw new ArgumentException("Error: " + Name + ".Merge(" + second.Name + ") is an invalid call - Names must be equal.");
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
						catch (TargetParameterCountException)
						{
							try
							{
								value = propertyInfo.GetValue(this, null);
								propertyInfo.SetValue(this, value, null);
							}
							catch (TargetParameterCountException)
							{
							}
						}
					}
				}
			}
			else Dirty = true;
		}
		public Item(string name, string comment, bool invalidate = true)
		{
			this.Name = name;
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