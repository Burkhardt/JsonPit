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


// TODO write tests for all operations 2024-07-05
// 
// change: JsonPit is a directory and a file by the same name
// i.e. ~/MyStuff/Nations/Nations.json and ~/MyStuff/Persons/Persons.json
// change files are in the same directory as the JsonPit file
// linked files can be in subdirectories or at the top level
// i.e. ~/MyStuff/Nations/img/finland.jpg or ~/MyStuff/Persons/BenFranklin.jpg
// the links from the JsonPit file would be relative to the JsonPit folder
// i.e. img/finland.jpg or BenFranklin.jpg

namespace JsonPit
{
	public class ItemsBase
	{
		/// <summary>Identifying name, i.e. JsonFileName from enclosing JsonFile</summary>
		public string key;
		public ItemsBase(string key = null)
		{
			this.key = key;
		}
	}
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
					configDirDefault = Os.winInternal($"{Os.CloudStorageRoot}Config{Os.DIRSEPERATOR}");
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
			// was: 
			//var info = new System.IO.FileInfo(Name);
			//if (!info.Exists)
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
		/// <example>~/MyStuff/Nations/Nations.json</example>
		/// 
		public PitFile JsonFile
		{
			get { return jsonFile; }
			set
			{
				jsonFile = value;
				// if (!file.Path.EndsWith(file.Name + Os.DIRSEPERATOR ))
				// 	file.Path += ConfigDirDefault;
				// jsonFile = file;
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
	public class TimestampedValue
	{
		/// <summary>
		/// Time: get may be deferred, set instantly
		/// </summary>
		/// <remarks>any set DateTimeOffset value will be converted to UniversalTime</remarks>
		public DateTimeOffset Time { get; set; }
		#region roundtrip lab DateTime and DateTimeOffset
		// DateTimeOffset.Parse("2016-04-15T18:01:04.6441809Z").UtcDateTime.ToString("o")
		// => "2016-04-15T18:01:04.6441809Z"
		// DateTime.Parse("2016-04-15T18:01:04.6441809+00:00").ToUniversalTime().ToString("o")
		// => "2016-04-15T18:01:04.6441809Z"
		// DateTimeOffset.Parse("2016-04-15T18:01:04.6441809Z").ToString("o")
		// => "2016-04-15T18:01:04.6441809+00:00"
		// DateTimeOffset.Parse("2016-04-15T18:01:04.6441809+00:00").ToString("o")
		// => "2016-04-15T18:01:04.6441809+00:00"
		#endregion
		public string Value { get; set; }
		/// <summary>
		/// string formats for time must be in format "o", parse in Time.get fails otherwise
		/// </summary>
		public override string ToString() => $"{Value}|{Time.UtcDateTime.ToString("o")}";   // writes Z instead of +00:00 see roundtrip lab
		/// <summary>constructor</summary>
		/// <param name="value"></param>
		/// <param name="time">internally uses DateTimeOffset.UtcNow if omitted</param>
		/// <remarks>converts null-value to "" because the file will store it the same way anyway</remarks>
		public TimestampedValue(object value, DateTimeOffset? time = null)
		{
			Value = value == null ? string.Empty : value.ToString();
			Time = time ?? DateTimeOffset.UtcNow;
		}
		/// <summary>
		/// Constructor from string, potentially with time appended as done by TimestampedValue.ToString()
		/// </summary>
		/// <remarks>Value gets assigned instantly, use format "o"</remarks>
		/// <param name="valueAndTime"></param>
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
	/// <summary>
	/// Short flag file to contain info about the Server and the last load date of the xml files that is flagged (usually in a subdirectory)
	/// </summary>
	/// <remarks>
	/// Flagfiles are associated with a particular server.
	/// This means that only one Server will ever be allowed write access to this file. Even if the file is inside a Dropbox
	/// (which it has to be for Persist to work), no other server can change the Server's flag file and only the assigned
	/// Master Server can change the Master.flag file. The Master.flag file is a special case because if the Server is changed the 
	/// owner of the Master.flag file changes accordingly(with the associated JsonFile).
	/// However, although there is just one "owning" server there can be various threads or even programs that try to write the 
	/// flagFile in the logical same time. This means the following:
	/// - concurrent write access is possible => I/O blocking can occur
	/// - the memory representation of the file's content can be wrong a split second after the file content was read
	/// In a most likely scenario, only one Application is granted write access to a JsonFile and therefore it's associated FlagFiles.
	/// Thus, a Monitor in this very process can avoid the concurrency and therefore the I/O Blocking.
	/// JsonFlagFile implements this Monitor for all Write accesses to the file, which means that as long as only one App is
	/// trying to change any JsonFile, no I/O blocking or other harmful collisions should occur.
	/// In a scenario where the same App is deployed to two paths inside the IIS (with or without a seperate Application Pool)
	/// other arrangements have to be made to make sure that no FlagFile is written by both processes/Apps concurrently.
	/// The used Monitor works through serializing/queueing all threads entering the Monitor. It's tied to a system wide variable
	/// that guards the file. This means that the variable has to be either static or to be located in some global storage like
	/// Application. We think that it would be of significant advantage to use a static Variable in the associated JsonFile's 
	/// typeparameter class.
	/// The current implementation uses a static variable in the JsonFlagFile class - which is more restrictive without being more 
	/// safe.
	/// Any JsonFlagFile can be read by any process. Therefore, any process who cannot or should not (by what was agreed on above)
	/// write to a JsonFlagFile can still consume it. This very file can change any second (and come in as a synchronization through Dropbox).
	/// This means that any read access to the JsonFlagFile has to come with a ReRead of the file. Every Read therefore creates I/O.
	/// Be careful when using the Properties .Server and .Time as get - it will cause a disk operation everytime you use it.
	/// Every setter and Update will not only cause one or two Reads but also a Write.
	/// </remarks>
	public class MasterFlagFile : TextFile
	{
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
		/// <summary>
		/// The originator for MasterFlagFile is a server
		/// </summary>
		public string Originator
		{
			get
			{
				// this property stays the same after initialization; only read if not never read before
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
		/// <summary>
		///  time is very volatile - re-reading the whole file (~20 Bytes) every time seams to be justified
		/// </summary>
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
		/// <summary>
		/// Use this instead of TimestampedValue.Time = newValue to optimize IO
		/// </summary>
		/// <param name="time"></param>
		/// <param name="originator"></param>
		/// <returns></returns>
		public TimestampedValue Update(DateTimeOffset? time = null, string originator = null)
		{
			var tv = new TimestampedValue(Originator, DateTimeOffset.UtcNow);
			if (string.IsNullOrEmpty(tv.Value))
			{
				// no server claimed to be master yet => claim it for the localhost
				tv.Value = Environment.MachineName;
			}
			if (!string.IsNullOrEmpty(originator))
			{
				// overruled: claim it for the passed-in server
				tv.Value = originator;
			}
			if (time != null)
			{
				// overruled: set the passed-in time
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
		public static string CurrentProcessId()
		{
			var p = System.Diagnostics.Process.GetCurrentProcess();
			return $"{p.ProcessName}:{p.Id}";
		}
		/// <summary>
		/// Process Name and Id; as opposed to Originator, Process can change in the background if more than one process on this server is using the same settings/items file; very volatile.
		/// </summary>
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
		/// <summary>
		/// Use this instead of TimestampedValue.Time = newValue to optimize IO
		/// </summary>
		/// <param name="time"></param>
		/// <param name="process"></param>
		/// <returns></returns>
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
	//#region Jay Hilyard & Stephen Teilhet dynamic solution
	//// https://books.google.com/books?id=ysmhCgAAQBAJ&pg=PA319&lpg=PA319&dq=extensible+DynamicBase%3CT%3E&source=bl&ots=k2ZGkxt4m_&sig=b4DwsKQas408LErgqGpwNlXwQSo&hl=en&sa=X&ved=0ahUKEwjQy-Sp457NAhXFMGMKHcAVC24Q6AEIHzAA#v=onepage&q&f=false
	//public class DynamicBase<T> : DynamicObject
	//	where T : new()
	//{
	//	private T _containedObject = default(T);
	//	[JsonExtensionData] //JSON.NET 5.0 and above
	//	private Dictionary<string, object> _dynamicMembers = new Dictionary<string, object>();
	//	private List<PropertyInfo> _propertyInfos = new List<PropertyInfo>(typeof(T).GetProperties());
	//	public DynamicBase()
	//	{
	//	}
	//	public DynamicBase(T containedObject)
	//	{
	//		_containedObject = containedObject;
	//	}
	//	public override bool TryInvokeMember(InvokeMemberBinder binder, object[] args, out object result)
	//	{
	//		if (_dynamicMembers.ContainsKey(binder.Name)
	//		&& _dynamicMembers[binder.Name] is Delegate)
	//		{
	//			result = (_dynamicMembers[binder.Name] as Delegate).DynamicInvoke(
	//				args);
	//			return true;
	//		}
	//		return base.TryInvokeMember(binder, args, out result);
	//	}
	//	public override IEnumerable<string> GetDynamicMemberNames() => _dynamicMembers.Keys;
	//	public override bool TryGetMember(GetMemberBinder binder, out object result)
	//	{
	//		result = null;
	//		var propertyInfo = _propertyInfos.Where(pi => pi.Name == binder.Name).LastOrDefault();
	//		// Make sure this member isn't a property on the object yet
	//		if (propertyInfo == null)
	//		{
	//			// look in the additional items collection for it
	//			if (_dynamicMembers.Keys.Contains(binder.Name))
	//			{
	//				// return the dynamic item
	//				result = _dynamicMembers[binder.Name];
	//				return true;
	//			}
	//		}
	//		else
	//		{
	//			// get it from the contained object
	//			if (_containedObject != null)
	//			{
	//				result = propertyInfo.GetValue(_containedObject);
	//				return true;
	//			}
	//		}
	//		return base.TryGetMember(binder, out result);
	//	}
	//	public override bool TrySetMember(SetMemberBinder binder, object value)
	//	{
	//		var propertyInfo = _propertyInfos.Where(pi => pi.Name == binder.Name).LastOrDefault();
	//		// Make sure this member isn't a property on the object yet
	//		if (propertyInfo == null)
	//		{
	//			// look in the additional items collection for it
	//			if (_dynamicMembers.Keys.Contains(binder.Name))
	//			{
	//				// set the dynamic item
	//				_dynamicMembers[binder.Name] = value;
	//				return true;
	//			}
	//			else
	//			{
	//				_dynamicMembers.Add(binder.Name, value);
	//				return true;
	//			}
	//		}
	//		else
	//		{
	//			// put it in the contained object
	//			if (_containedObject != null)
	//			{
	//				propertyInfo.SetValue(_containedObject, value);
	//				return true;
	//			}
	//		}
	//		return base.TrySetMember(binder, value);
	//	}
	//	public override string ToString()
	//	{
	//		StringBuilder builder = new StringBuilder();
	//		foreach (var propInfo in _propertyInfos)
	//		{
	//			if (_containedObject != null)
	//				builder.AppendFormat("{0}:{1}{2}", propInfo.Name, propInfo.GetValue(_containedObject), Environment.NewLine);
	//			else
	//				builder.AppendFormat("{0}:{1}{2}", propInfo.Name, propInfo.GetValue(this), Environment.NewLine);
	//		}
	//		foreach (var addlItem in _dynamicMembers)
	//		{
	//			// exclude methods that are added from the description
	//			Type itemType = addlItem.Value.GetType();
	//			Type genericType = itemType.IsGenericType ? itemType.GetGenericTypeDefinition() : null;
	//			if (genericType != null)
	//			{
	//				if (genericType != typeof(Func<>) && genericType != typeof(Action<>))
	//					builder.AppendFormat("{0}:{1}{2}", addlItem.Key, addlItem.Value, Environment.NewLine);
	//			}
	//			else
	//				builder.AppendFormat("{0}:{1}{2}", addlItem.Key, addlItem.Value, Environment.NewLine);
	//		}
	//		return builder.ToString();
	//	}
	//}
	//#endregion
	public enum Compare { JSON, ByProperty };
	public class PitItem : JObject
	{
		//public static explicit operator JObject(PitItem pitItem)
		//{
		//	return new JObject();
		//}
		public string Name
		{
			get { return (string)this[nameof(Name)]; }
			set { this[nameof(Name)] = value; }
		}
		/// <summary>has to be set to DateTimeOffset.UtcNow explicitely</summary>
		public DateTimeOffset Modified
		{
			get
			{
				return (DateTimeOffset)this[nameof(Modified)];
			}
			set
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
				//if (ChildrenTokens.Contains()) Properties
				// check if Deleted exists ... why did it not exist in TestStorePit?
				return (bool)this[nameof(Deleted)];
			}
			set { this[nameof(Deleted)] = value; }
		}
		public string Note
		{
			get { return (string)this[nameof(Note)]; }
			set { this[nameof(Note)] = value; }
		}
		/// <summary>
		/// add or set a property of this PitItem with value - keeps all other properties
		/// </summary>
		/// <param name="objectAsJsonString">one property as JSON, i.e. { "Subscriber": "demo" } or { "address": { "street": "1 Main St", "city": "A Town" } }</param>
		public void SetProperty(string objectAsJsonString)
		{
			Deleted = false;
			Invalidate();
			foreach (var kvp in JObject.Parse(objectAsJsonString))
			{
				this[kvp.Key] = kvp.Value;
				//break;	not any more - can now be used to assign all properties of objects of subclasses // use just the first property of the passed-in Json-Object
			}
		}
		/// <summary>
		/// add or set a property of this PitItem with value - keeps all other properties
		/// </summary>
		/// <param name="obj">i.e. new { Subscriber = SelectedValue }</param>
		public void SetProperty(object obj)
		{
			SetProperty(JSON.SerializeDynamic(obj));
		}
		/// <summary>
		/// add or set a property of this PitItem with value - keeps all other properties
		/// </summary>
		/// <param name="propertyName">name of property</param>
		public void DeleteProperty(string propertyName)
		{
			Deleted = false;  // the PitItem is not Deleted
			Invalidate();
			this[propertyName] = null;
		}
		/// <summary>
		/// Mark as deleted
		/// </summary>
		/// <param name="by"></param>
		/// <param name="backDate100"></param>
		/// <returns>false if it was deleted already, true otherwise</returns>
		public bool Delete(string by = null, bool backDate100 = true)
		{
			if (Deleted)
				return false;   // means: was deleted already
			Deleted = true;
			if (backDate100)
				Modified = DateTimeOffset.UtcNow - new TimeSpan(0, 0, 0, 100);  // backdate delete for 100ms
			Invalidate(preserveTimestamp: backDate100); // changes modified
			var s = $"[{Modified.ToUniversalTime().ToString("u")}] deleted";
			if (!string.IsNullOrEmpty(by))
				s += " by " + by;
			Note = s + ";\n" + Note;
			return true;
		}
		/// <summary>
		/// means: PitItem was modified from the original state of the setting as it was once loaded from disk; 
		/// in a concurrent environment this does not necessarily mean that the current setting on disk has (still) an older value
		/// since the file could have been updated on any other machine and synchronized back to this machine.
		/// Use merge to get the youngest value - merge also adjusts the dirty flag accordingly.
		/// </summary>
		protected bool Dirty { get; set; }  // protected makes it non-persistent
		virtual public bool Valid() { return !Dirty; }
		/// <summary>call to indicate that the memory representation of the PitItem now equals the file representation</summary>
		virtual public void Validate() { Dirty = false; }
		/// <summary>call to indicate that the memory representation of the PitItem differs from the file representation</summary>
		/// <param name="preserveTimestamp">does not update modified (only sets the dirty flag) if true</param>
		virtual public void Invalidate(bool preserveTimestamp = false)
		{
			Dirty = true;
			if (!preserveTimestamp)
				Modified = DateTimeOffset.UtcNow;
		}
		public override string ToString()
		{
			var jsonSerializerSettings = new JsonSerializerSettings() { DateTimeZoneHandling = DateTimeZoneHandling.Utc };
			return JsonConvert.SerializeObject(this, jsonSerializerSettings); //return JSON.SerializeDynamic(this, JsonPitBase.jilOptions);
		}
		/// <summary>Constructor</summary>
		/// <param name="name"></param>
		/// <param name="comment"></param>
		/// <param name="invalidate"></param>
		/// <remarks>timestamp of this setting will be set to UtcNow after this</remarks>
		public PitItem(string name, bool invalidate = true, string comment = "")
		{
			this.Name = name;
			Note = comment;
			if (invalidate)
				Invalidate();
			Deleted = false;
		}
		/// <summary>Constructor</summary>
		/// <param name="name"></param>
		/// <param name="extendWith">i.e. new { someProperty = "some value" }</param>
		/// <param name="comment">optional comment for this PitItem object</param>
		/// <remarks>will add internal properties like timestamp</remarks>
		public PitItem(string name, object extendWith, string comment = "")
			: this(name, JSON.SerializeDynamic(extendWith), comment)
		{
		}
		/// <summary>Constructor</summary>
		/// <param name="name"></param>
		/// <param name="extendWithAsJson">i.e. @"{""gender"": ""f""}"</param>
		/// <param name="comment"></param>
		/// <remarks>timestamp of this setting will be set to UtcNow after this</remarks>
		public PitItem(string name, string extendWithAsJson, string comment = "")
		{
			this.Name = name;
			Note = comment;
			Invalidate();
			Deleted = false;
			foreach (var token in JObject.Parse(extendWithAsJson))
				this[token.Key] = token.Value;
		}
		/// <summary>Constructor</summary>
		/// <param name="name"></param>
		/// <param name="comment"></param>
		/// <param name="invalidate"></param>
		/// <param name="timestamp"></param>
		/// <remarks>timestamp of this setting will be set to UtcNow after this</remarks>
		public PitItem(string name, bool invalidate, DateTimeOffset timestamp, string comment = "")
			: this(name, invalidate, comment)
		{
			Modified = timestamp;
		}
		/// <summary>copy-constructor</summary>
		/// <param name="other"></param>
		/// <param name="timestamp">null means use the one from other </param>
		/// <remarks>timestamp will be set to from's timestamp after this</remarks>
		public PitItem(PitItem other, DateTimeOffset? timestamp = null)
			: base(other) // copy everything - done by base class already
		{
			Modified = timestamp == null ? (DateTimeOffset)other[nameof(Modified)] : (DateTimeOffset)timestamp; // setting the properties altered the timestamp; re-set it to from's 
		}
		/// <summary>
		/// Copy constructor - sets the dirty flag but keeps Deleted, Modified, Name, and Note unchanged
		/// </summary>
		/// <param name="from"></param>
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
				if (Deleted) Console.WriteLine(ex); // to be able to see ex in the debuggercd ..
			}
			Dirty = true;
			try 
			{
				// if it comes from a json string it might not have the Modified property
				Modified = (DateTimeOffset)this[nameof(Modified)];
			}
			catch (Exception )
			{
				Modified = DateTimeOffset.UtcNow;	// will be added here
			}
			Name = (string)this[nameof(Name)];
			Note = (string)this[nameof(Note)];
		}
		public PitItem()
		{
		}
	}
	/// <summary>
	/// 
	/// </summary>
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
		static public long dtSharp = 0; //30;	// distance of this amount of ticks or less means equal - 0 means exact
		/// <summary>
		/// compares timestamps to be almost the same
		/// </summary>
		/// <param name="dto1"></param>
		/// <param name="dto2"></param>
		/// <returns></returns>
		static public bool isLike(this DateTimeOffset dto1, DateTimeOffset dto2)
		{
			return Math.Abs(dto1.UtcTicks - dto2.UtcTicks) <= dtSharp;
		}
		/// <summary>
		/// Use this if you want to aling timestamps that are similar - uses isLike()
		/// </summary>
		/// <param name="dto1"></param>
		/// <param name="dto2"></param>
		/// <returns></returns>
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
	public class PitItems : ItemsBase, IEnumerable<PitItem>
	{
		public List<PitItem> Items = new List<PitItem>();
		private void Sort(bool preserve = true)
		{
			if (Items == null || Items.Count == 0)
				return;
			var q = from _ in Items select _;
			var comparer = new PitItemEqualityComparer();
			var distinct = q.Distinct(comparer);            // TODO: debug this and check if it works for some test cases
			var sorted = distinct.OrderBy(x => x.Modified);
			#region make sure the dirty flag is set according to preserve since the history has changed although nothing has changed in the top position
			sorted.Last().Invalidate(preserve);
			#endregion
			Items = sorted.ToList();
		}
		#region Stack methods for List to simulate the logic that the most recent item is retrieved by Peek() and that new items are pushing the older ones to the past
		/// <summary>
		///  insert new element if it was not there already and keep Items ordered by Modified
		/// </summary>
		/// <param name="item"></param>
		/// <param name="preserve"></param>
		public void Push(PitItem item, bool preserve = true)
		{
			if (Items == null)
				Items = new List<PitItem>();
			if (Items.Count() > 0 && Peek().Modified > item.Modified) // new item is older than the latest
			{
				#region
				item.Invalidate(preserve);
				// TODO inefficient solution; find insert point would be faster since the Stack was already sorted
				Items.Add(item);   // push it in anyway; invalidating anything but the top is not helping
				Sort(preserve);
				#endregion
			}
			else
			{
				item.Invalidate(preserve);
				Items.Add(item);
			}
			#region consider MaxCount
			if (MaxCount > 0)
			{
				while (Items.Count() > MaxCount)
					Items.RemoveAt(0);
			}
			#endregion
		}
		public PitItem Peek(DateTimeOffset? timestamp = null)
		{
			if (Count == 0)
				return default(PitItem);
			if (timestamp == null)
				return Items.Last();
			for (int i = Count - 1; i > -1; i--)
				if (timestamp > Items[i].Modified)
					return Items[i];
			return default(PitItem);    //			return Items.Last();
		}
		public JObject Get(DateTimeOffset? timestamp = null)
		{
			return Peek(timestamp);
		}
		public int MaxCount { get; set; } = 5;
		public int Count => Items.Count;
		#endregion
		public IEnumerator<PitItem> GetEnumerator()
		{
			return ((IEnumerable<PitItem>)Items).GetEnumerator();
		}
		IEnumerator IEnumerable.GetEnumerator()
		{
			return ((IEnumerable<PitItem>)Items).GetEnumerator();
		}
		public PitItems(IEnumerable<PitItem> fromCollection)
		{
			foreach (var item in fromCollection)
				Push(item);
			Sort();
			this.key = key ?? Items.First().Name;
		}
		public PitItems()
		{
		}
		public PitItems(string key = null, IEnumerable<PitItem> value = null, bool preserve = true) : base(key)
		{
			Items = value == null ? new List<PitItem>() : value.ToList();
			Sort(preserve);
			this.key = key ?? Items.First().Name;
		}
		/// <summary>
		/// joins all, removes duplicates, sorts
		/// </summary>
		/// <param name="pSet2"></param>
		public void Merge(PitItems pSet2)
		{
			foreach (var item in pSet2)
				Push(item); // only inserts if it wasn't there already
			Sort();
		}
	}
	/// <summary>
	/// JsonPit is a file that contains Items
	/// </summary>
	public class Pit : JsonPitBase, IEnumerable<PitItems>
	{
		private Func<PitItem, string> orderBy;
		/// <summary>Createsfile names like C:/Dropbox/demo/DyBrands.json</summary>
		/// <param name="subscriber">acts as subdirectory</param>
		/// <param name="pit">works like type name for DynamicItem - affects the file's name</param>
		/// <param name="version">"" => get version from JsonPit module; null => no version in path</param>
		/// <returns>FullName</returns>
		public static string defaultPitName(string pit, string subscriber, string version = null)
		{	//deprecated, don't use
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
		#region overload to put meaning to it
		/// <summary>
		/// return the latest Modified of all
		/// </summary>
		/// <returns></returns>
		public override DateTimeOffset GetMemChanged() => GetLastestItemChanged();
		#endregion
		#region compare with or without ignoreCase
		private StringComparer Comparer
		{
			get
			{
				return ignoreCase ? StringComparer.InvariantCultureIgnoreCase : StringComparer.InvariantCulture;
			}
		}
		private StringComparison Comparison
		{
			get
			{
				return ignoreCase ? StringComparison.InvariantCultureIgnoreCase : StringComparison.InvariantCulture;
			}
		}
		public void ConsiderCase()
		{
			if (ignoreCase)
			{
				ignoreCase = false;
				if (HistoricItems != null)
					HistoricItems = new ConcurrentDictionary<string, PitItems>(HistoricItems, ignoreCase ? StringComparer.InvariantCulture : StringComparer.InvariantCultureIgnoreCase);
			}
		}
		public void IgnoreCase()
		{
			if (!ignoreCase)
			{
				ignoreCase = true;
				if (HistoricItems != null)
					HistoricItems = new ConcurrentDictionary<string, PitItems>(HistoricItems, ignoreCase ? StringComparer.InvariantCulture : StringComparer.InvariantCultureIgnoreCase);
			}
		}
		private bool ignoreCase = false;
		#endregion
		public /*internal*/ ConcurrentDictionary<string, PitItems> HistoricItems = new ConcurrentDictionary<string, PitItems>();
		public ICollection<string> Keys => HistoricItems.Keys;
		public bool ContainsKey(string key) => HistoricItems.ContainsKey(key);
		public bool Contains(string itemName, bool withDeleted = false)
		{
			#region Testcase TestProfileMerge seems to need this
			//if (items == null)
			//	items = new ConcurrentDictionary<string, T>(Comparer);
			#endregion
			var isThere = HistoricItems.Keys.Contains(itemName, Comparer); // settings.ContainsKey(settingName);
			if (withDeleted)
				return isThere;
			return isThere && !HistoricItems[itemName].Last().Deleted;  // the item is considered deleted only if the latest value is marked as deleted
		}
		//public PitItemMemory Infos { get; set; }
		/// <summary>
		/// Checks settings in the container
		/// </summary>
		/// <returns>true if any is not Valid, false if none</returns>
		public bool Invalid()
		{
			//if (changes > 0)
			//	return true;
			var query = from kvp in HistoricItems where !kvp.Value.Peek().Valid() select kvp.Value.Peek().Name;
			return query.Count() > 0;
		}
		/// <summary>
		/// latest change - does not consider the surrounding container, just the settings
		/// </summary>
		/// <returns>timestamp, DateTimeOffset.MinValue of none</returns>
		public DateTimeOffset GetLastestItemChanged()
		{
			var list = (from kvp in HistoricItems select kvp.Value.Peek().Modified);
			list = list.OrderByDescending(x => x);
			return list.Count() > 0 ? list.Last() : DateTimeOffset.MinValue;
		}
		/// <summary>
		/// gets the "top" item of the values as a stack (history, most recent)
		/// </summary>
		/// <param name="key"></param>
		/// <returns></returns>
		public PitItem this[string key]
		{
			get
			{
				if (!HistoricItems.ContainsKey(key) || HistoricItems[key].Count == 0
					|| HistoricItems[key].Items == null || HistoricItems[key].Peek().Deleted)
					return default(PitItem);
				return HistoricItems[key].Peek();
			}
		}
		/// <summary>
		/// Add a PitItem; use value.Name as index key for history
		/// </summary>
		public PitItem PitItem
		{
			set
			{
				Add(value);
			}
		}
		public dynamic ItemProperty
		{
			set
			{
				var pitItem = this[value.Name];
				pitItem.SetProperty(value);
				this.PitItem = pitItem;
			}
		}
		/// <summary>add one setting at the end; same as array operator setter but with extra parameter preserve</summary>
		/// <param name="item"></param>
		/// <param name="preserve">set true, if you want to add a new or recovered setting and preserve the item's deleted and modified</param>
		/// <remarks>don't add item if the latest item in the history has the same timestamp</remarks>
		/// <returns>false, if the item was not pushed (identical timestamps of current value and new value</returns>
		public bool Add(PitItem item, bool preserve = true)
		{
			var list = HistoricItems.ContainsKey(item.Name) ? HistoricItems[item.Name] : null;
			if (list != null)
			{
				var q = from listItem in list where listItem.Equals(item) select listItem.Name;
				if (q.Count() > 0)
					return false;   // nothing added because this same listItem was already there
			}
			else list = new PitItems();
			item.Invalidate(preserve);  // just set the Dirty flag, Modified is preserved
			list.Push(item, preserve);
			return HistoricItems.TryAdd(item.Name, list);  // assume that sb else added the itemStack if result false
		}
		/// <summary>logical delete; sets deleted flag</summary>
		/// <param name="itemName"></param>
		/// <param name="by">who deleted this item; used to write by into the item's Note</param>
		/// <param name="backDate">todo: describe backDate parameter on Delete</param>
		/// <returns>true: did not and still does not exist or is marked as deleted now</returns>
		/// <remarks>when querying HistoricItems, each property of an item is considered to have the value null if the Deleted flag is set for this item</remarks>
		public bool Delete(string itemName, string by = null, bool backDate = true)
		{
			if (string.IsNullOrEmpty(itemName))
				return true;
			try
			{
				var item = HistoricItems[itemName].Last();
				if (item.Delete(by, backDate))  // will not delete a deleted item again (conserves timestamp and note)
					PitItem = item; // invalidates
			}
			catch (KeyNotFoundException) { }
			catch (Exception)
			{
				return false;
			}
			return true;
		}
		/// <summary>
		/// Get the latest PitItem for this key as JObject
		/// </summary>
		/// <param name="key"></param>
		/// <param name="withDeleted"></param>
		/// <remarks>converts better to dynamic</remarks>
		/// <returns>default(PitItem) or the most recent PitItem as JObject</returns>
		public JObject Get(string key, bool withDeleted = false)
		{
			if (withDeleted)
				return HistoricItems[key].Peek();
			return (JObject)this[key];
		}
		/// <summary>
		/// Extracts the values of one property of a PitItem over time
		/// </summary>
		/// <param name="oName">name of the object</param>
		/// <param name="pName">name of the property</param>
		/// <returns>IEnumerable with all timestamped values of this property as found in the JsonPit, everything else filtered away</returns>
		public IEnumerable<KeyValuePair<DateTimeOffset, JToken>> ValuesOverTime(string oName, string pName)
		{
			if (!HistoricItems.ContainsKey(oName))
				return Enumerable.Empty<KeyValuePair<DateTimeOffset, JToken>>();
			var q = from item in HistoricItems[oName].Items
					select new KeyValuePair<DateTimeOffset, JToken>(item.Modified, item.Deleted ? null : (JToken)item[pName]);
			return q;
		}
		/// <summary>
		/// For facets that are known to store a list of values
		/// </summary>
		/// <param name="oName"></param>
		/// <param name="pName"></param>
		/// <returns>an IEnumerable of KeyValuePairs; the value part will be a List of JToken</returns>
		/// <remarks>this way, the array operator can be used directly as expected</remarks>
		// <see cref="TestProfileItem"/> 
		public IEnumerable<KeyValuePair<DateTimeOffset, List<JToken>>> ValueListsOverTime(string oName, string pName)
		{
			if (!HistoricItems.ContainsKey(oName))
				return Enumerable.Empty<KeyValuePair<DateTimeOffset, List<JToken>>>();
			var values = ValuesOverTime(oName, pName);
			var q = (from kvp in values
					 select new KeyValuePair<DateTimeOffset, List<JToken>>(kvp.Key, (from _ in (JArray)kvp.Value select _).ToList()));
			return q;
		}
		/// <summary>
		/// collect just the most recent values - no history, without deleted; 
		/// </summary>
		/// <remarks>this runs a query and copies the values; rather use array operator of Pit or PitItems if possible</remarks>
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
		/// <summary>
		/// Load Json file
		/// </summary>
		/// <param name="undercover">todo: describe undercover parameter on Load</param>
		/// <param name="preserve"></param>
		public void Load(bool undercover = false, bool preserve = true)
		{
			{
				try // http://www.newtonsoft.com/json/help/html/SerializingJSON.htm
				{
					var jsonArrayOfArrayOfObject = File.ReadAllText(JsonFile.FullName);
					#region quick analyze content
					for (int i = 0, square = 0; i < jsonArrayOfArrayOfObject.Length && i < 100 && square < 2; i++)
					{
						if (jsonArrayOfArrayOfObject[i] == '[')
							square++;
						else if (jsonArrayOfArrayOfObject[i] == '{')
							throw new FormatException("JSON file format is not compatible with JsonPit");
					}
					#endregion
					if (!string.IsNullOrEmpty(jsonArrayOfArrayOfObject))
					{
						initValues(JArray.Parse(jsonArrayOfArrayOfObject), preserve);
					}
				}
				catch (InvalidOperationException)
				{
					throw;
				}
				finally
				{
					if (!(undercover || unflagged))
						ProcessFlag().Update(GetLastestItemChanged());
					Interlocked.Exchange(ref usingPersistence, 0);
				}
			}
		}
		/// <summary>makes JsonFile persistent; performs merge on item level</summary>
		/// <param name="force">todo: describe force parameter on Store</param>
		/// <param name="pretty"></param>
		/// <param name="indentChar"></param>
		protected void Store(bool force = false, bool pretty = false, char indentChar = '\t')
		{
			if (HistoricItems == null)
				return;
			#region only save if necessary
			if (force || Invalid())
			{
				if (ReadOnly)
					throw new IOException("JsonFile " + JsonFile.Name + " was set to readonly mode but an attempt was made to execute JsonFile.Store");
				Exception inner = null;
				if (HistoricItems.Count() > 0)    // passing in empty settings is not a valid way to delete the content of a settings file; silently refuse storing a new version
				{
					if (Backup)
						JsonFile.backup();
					else JsonFile.rm();
					JsonFile.mkdir();  // does nothing if dir was there; otherwise creates the dir and awaits materialization

					#region buffered writing with JsonTextWriter
					using (FileStream fs = File.Open(JsonFile.FullName, FileMode.CreateNew))
					{
						using (StreamWriter sw = new StreamWriter(fs))
						{
							using (JsonTextWriter jw = new JsonTextWriter(sw))
							{
								jw.Formatting = pretty ? Formatting.Indented : Formatting.None;
								jw.IndentChar = indentChar;
								jw.Indentation = 1;
								var serializer = new JsonSerializer();
								//serializer.DateParseHandling = DateParseHandling.DateTimeOffset;
								serializer.DateFormatHandling = DateFormatHandling.IsoDateFormat;
								serializer.Serialize(jw, this);
							}
						}
					}
					#endregion
					#region store dynamic objects - currently using Jil
					////var items = descending ?
					////	(from item in Infos.ItemsAsObjects select item).OrderByDescending<dynamic, string>(x => x.Name) :
					////	(from item in Infos.ItemsAsObjects select item).OrderBy<dynamic, string>(x => x.Name);
					////// TODO use OrderBy predicate
					////var s = JSON.SerializeDynamic(items, jilOptions);
					//{
					//	var outer = new List<PitItem[]>();
					//	PitItem[] valArray = null;
					//	foreach (var stack in Infos)
					//	{
					//		valArray = (from o in stack select o).ToArray();
					//		outer.Add(valArray);
					//	}
					//	var s = JsonConvert.SerializeObject(outer);
					//	File.WriteAllText(Name, s);
					//}
					#endregion

					var changeTime = GetLastestItemChanged();
					File.SetLastWriteTimeUtc(JsonFile.FullName, changeTime.DateTime);
					if (!unflagged)
					{
						var masterFlag = MasterFlag();
						masterFlag.Update(changeTime);      // set the new JsonFile timestamp to the flag file master.info
						ProcessFlag().Update(changeTime);   // make sure the server's flag file also has the date current
					}
					#region Validate each setting
					// 	means: this setting has been stored to disk; however, in a concurrent environment it does not mean, 
					// that the setting on disk still has the same value as the one in memory since it could have been changed 
					// elsewhere and synchronized back to this machine
					foreach (var kvp in HistoricItems)
					{
						kvp.Value.Peek().Validate();
					}
					#endregion
				}
				else
				{
					throw new IOException("attempt to write a JsonFile with no items into "
					+ JsonFile.FullName + "; the current memory representation should reflect the old file's items which it does not!", inner);
				}
			}
			#endregion
			//Infos.Validate();
		}
		/// <summary>makes File persistent - thread safe</summary>
		/// <param name="backup">backs up current xml file if true, or not if false, or uses the item passed in to the constructor if null</param>
		/// <param name="force">todo: describe force parameter on Save</param>
		/// <remarks>function varies across master and others: master can store to the actual file - others can only create change files
		/// </remarks>
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
		/// <summary>
		/// Save all changes as a single change file per setting
		/// </summary>
		/// <remarks>To determine what has changed, reloads the disk file's settings into a second variable and compares one by one with the ones in this.Infos.
		/// Writes a ChangeFile for a setting only if it is newer than the setting from disk. The time stamp of the change file has to be the modified time stamp in the setting.
		/// Does not care about settings that are newer on disk.</remarks>
		/// <seealso cref="Load"/>
		/// <seealso cref="MergeChanges"/>
		private void CreateChangeFiles()
		{
			var compareFile = new Pit(JsonFile.FullName, undercover: true, unflagged: true);  // if undercover, Load does not update the time stamp in the flag file for the local server, ie RAPTOR133.info
			foreach (var name in Keys)
			{
				// FIX: sth goes wrong in the following line or in the line before foreach;
				// the last item receives a UtcNow timestamp => timestamp needs to be unchanged in comparison to the one(s) stored in the file
				if (!compareFile.ContainsKey(name) // means: item does not exist on disk
					|| (HistoricItems[name].Last().Modified > compareFile.HistoricItems[name].Last().Modified)) // memSetting is newer than diskSetting
					CreateChangeFile(HistoricItems[name].Last());
			}
		}
		/// <summary>
		/// A change file is a file that contains just one setting that can be merged into a file with many settings
		/// </summary>
		/// <param name="item"></param>
		/// <param name="machineName">any machine name different from Environment.MachineName (default)</param>
		/// <remarks>
		/// new: 2016-08-16 for JsonPit wrap it in [[item]]
		/// new: 2014-11-24 one change file must be sufficient for all servers, Dropbox distributes it;
		/// make sure change file name contains the server who originated the change
		/// for this to make sense, the JsonFile (with all settings) has to be located inside a dropbox and it's path has to contain the Environment.MachineName
		/// old: D:\Dropbox\Config\3.3.3\U17138031\Servers.json with change file D:\Dropbox\Config\3.3.3\Titan562\Servers\U17138031_635284457032693173.json
		/// new: D:\Dropbox\Config\3.6\Users.json with change file D:\Dropbox\Config\Users\635284457032693173_U17138031.json
		/// </remarks>
		public void CreateChangeFile(PitItem item, string machineName = null)
		{
			if (machineName == null)
				machineName = Environment.MachineName;
			var items = new JArray(); // PitItemMemory();
			var inner = new JArray();
			inner.Add(item);
			items.Add(inner);
			var changeFile = new RaiFile(ChangeDir + item.Modified.UtcTicks.ToString() + "_" + machineName + ".json");
			if (!File.Exists(changeFile.FullName))  // if the same file already exists it must contain the same change => no need to duplicate it
				new Pit(items, changeFile.FullName, unflagged: true, readOnly: false).Save();
		}
		/// <summary>
		/// Loads all changes and updates the file if necessary; run after Load
		/// </summary>
		/// <remarks>only the master is supposed to alter the main file or to delete change files; 
		/// new: change files now also have to be at least 10 min old to give all Dropbox instances a chance to do their sync
		/// and: only the master is allowed to perform deletion of ChangeFiles or updating of the main file on disk</remarks>
		public void MergeChanges()
		{
			var changes = new PitItems();
			#region collect changes
			Pit changePit;
			if (Directory.Exists(ChangeDir))
			{
				foreach (var file in Directory.GetFiles(ChangeDir, "*.json").OrderByDescending(x => x)) // gets the latest first => less work if several changes exist
				{
					try
					{
						changePit = new Pit(file, undercover: true);
						Pit pit;
						foreach (var pitItems in this)
						{
							pit = new Pit(pitDirectory: file);
							foreach (var changeItems in pit)
								pitItems.Merge(changeItems);
						}
						if (RunningOnMaster() && (DateTimeOffset.UtcNow - (new System.IO.FileInfo(file)).CreationTime).TotalSeconds > 600)
							if (!ReadOnly)
								new RaiFile(file).rm(); // savely remove the file from the dropbox unless in ReadOnly mode
					}
					catch (System.InvalidOperationException)
					{
					}
				}
			}
			#endregion
			#region
			if (!ReadOnly)
				Store();    // changes the disk file if a change file existed with a newer version of a item or if i.e. a ram item was changed/deleted
			#endregion
		}
		/// <summary>
		/// Reloads an JsonPit and all it's changes from disk if necessary (changes this.Infos)
		/// </summary>
		/// <returns>true if a reload was performed (and Infos was changed), false otherwise (Infos unchanged)</returns>
		public bool Reload()
		{
			var masterUpdates = MasterUpdatesAvailable();
			var foreignChanges = ForeignChangesAvailable();
			//bool ownChanges = this.Infos.Invalid();
			if (masterUpdates && RunningOnMaster())
			{
				//throw new DataMisalignedException($"Some process changed the main file without permission => inconsistent data in {nameof(Reload)}, file {this.Name}");
				throw new Exception($"Some process changed the main file without permission => inconsistent data in {nameof(Reload)}, file {JsonFile.Name}");
			}
			if (masterUpdates)
			{   // we need the new master; let's save our changes first
				Save(); // creates change files with changes that might loose if the master also has the same item changed
				Load(); // now we are guaranteed to have the latest stuff including all foreign changes
				return true;
			}
			if (!masterUpdates && foreignChanges)
			{ // just read the change files, not the main file; the items from the main file we already have in memory
				MergeChanges(); // whatever comes from disk is valid thereafter; a newer change from disk removes a older change in memory
								//Save();	// no, doppelgemoppelt; MergeChanges performs a Save at the end if Invalid()
				return true;
			}
			if (Invalid())
			{
				Save(); // changes Infos in the way that Infos.Invalid() will be false after Save()
				return true;
			}
			return false;
		}
		IEnumerator IEnumerable.GetEnumerator()
		{
			//return items.Values.GetEnumerator();
			foreach (var item in HistoricItems)
				yield return item.Value;
		}
		public IEnumerator<PitItems> GetEnumerator()
		{
			foreach (var kvp in HistoricItems)
				yield return kvp.Value;
		}
		private void initValues(JArray values, bool preserve = true)
		{
			PitItems stack;
			dynamic top;
			foreach (JArray inner in values)
			{
				if (inner.HasValues)
				{
					// looks like Modified does not get converted in a DateTimeOffset in UTC (Zulu notation)
					var q = from o in inner select new PitItem((JObject)o);
					stack = new PitItems(q.Last().Name, q, preserve); // new PitItem((dynamic)o)); // (PitItem)o);	// reminder: implement explicit cast
					top = (dynamic)inner.Last();
					HistoricItems.TryAdd((string)top.Name, stack);
				}
			}
		}
		private void initValues(IEnumerable<PitItems> values)
		{
			if (values != null && values.Count() > 0)
			{
				PitItems stack;
				dynamic top;
				foreach (var pitItems in values)
				{
					if (pitItems.Count > 0)
					{
						var q = from o in pitItems select new PitItem(o);
						stack = new PitItems(q.Last().Name, q); // new PitItem((dynamic)o)); // (PitItem)o);	// reminder: implement explicit cast
						top = (dynamic)pitItems.Last();
						HistoricItems.TryAdd((string)top.Name, stack);
					}
				}
			}
		}
		/// <summary>Get or Add whole List; list contains most recent items including deleted ones</summary>
		/// <remarks>use SettingNames or Select to get a list without deleted entries; does not return the history of an item</remarks>
		/// <param name="autoload"></param>
		/// <param name="backup"></param>
		/// <param name="descending"></param>
		/// <param name="ignoreCase"></param>
		/// <param name="orderBy"></param>
		/// <param name="pitDirectory">the path can include the pitName; will be assumed otherwise,i.e. ~/People/ => ~/People/People.json</param>
		/// <param name="readOnly"></param>
		/// <param name="subscriber"></param>
		/// <param name="undercover"></param>
		/// <param name="unflagged"></param>
		/// <param name="values">as returned by e.g. JArray.Parse(File.ReadAllText(fName))</param>
		/// <param name="version">"" (default) for get the version from the code; null for no version in path</param>
		public Pit(string pitDirectory, IEnumerable<PitItems> values = null, string subscriber = null, Func<PitItem, string> orderBy = null, bool descending = false,
						bool readOnly = true, bool backup = false, bool undercover = false, bool unflagged = false, bool autoload = true, bool ignoreCase = false, string version = "")
			: base(readOnly, backup, unflagged, descending)
		{
			JsonFile = new PitFile(pitDirectory);	// @TODO consider using the subscriber in the JsonFile path or name
			Subscriber = subscriber;
			this.orderBy = orderBy ?? new Func<PitItem, string>(x => x.Name);
			this.descending = descending;
			HistoricItems = new ConcurrentDictionary<string, PitItems>();
			#region values treatment
			initValues(values);
			#endregion
			if (autoload)   // new option autoload: false reduces file io if Reload is not necessary
			{
				if (JsonFile.Exists())
					Load(undercover, preserve: true);   // does not pick up the change files
				MergeChanges(); // this one does, calls Store()
				#region replaces Merge() 
				// TODO sth went probably missing here
				//if (memoryChanges != null)
				//{
				//	foreach (var stack in memoryChanges)
				//		foreach (var item in stack)
				//			Add(item, true);
				//	if (Invalid())    // can happen if the loop above found newer items in values
				//		Save(); // writes main file or changeFiles
				//}
				#endregion
			}
		}
		public Pit(JArray values, string pitDirectory, string subscriber = null, Func<PitItem, string> orderBy = null, bool descending = false,
				bool readOnly = true, bool backup = false, bool undercover = false, bool unflagged = false, bool autoload = true, bool ignoreCase = false, string version = "")
			: this(pitDirectory, Enumerable.Empty<PitItems>(), subscriber, orderBy, descending, readOnly, backup, undercover, unflagged, autoload, ignoreCase, version)
		{
			initValues(values);
		}
		///// <summary>
		///// Construct from Json.Net's JArray
		///// </summary>
		///// <param name="values">as returned by e.g. JArray.Parse(File.ReadAllText(fName))</param>
		//public Pit(string key, JArray values, bool ignoreCase = false)
		//	: base(key)
		//{
		//	this.ignoreCase = ignoreCase;
		//	HistoricItems = new ConcurrentDictionary<string, PitItems>();
		//	PitItems stack;
		//	dynamic top;
		//	foreach (JArray inner in values)
		//	{
		//		if (inner.HasValues)
		//		{
		//			var q = from o in inner select new PitItem((JObject)o);
		//			stack = new PitItems(q.Last().Name, q); // new PitItem((dynamic)o)); // (PitItem)o);	// reminder: implement explicit cast
		//			top = (dynamic)inner.Last();
		//			HistoricItems.TryAdd((string)top.Name, stack);
		//		}
		//	}
		//}
		/// <summary>
		/// Destructor - make sure the ChangeFile or the main file are up to date once the Pit gets disposed from Memory
		/// </summary>
		/// http://stackoverflow.com/questions/20065780/do-zombies-exist-in-net/20067933?s=23|0.0764#20067933
		~Pit()
		{
			//Debug.WriteLine($"in ~Pit - {MemoryKey} - {PitName}");
			if (!ReadOnly)
			{
				Save(backup: true, force: false);
				Debug.WriteLine($"{JsonFile.Name} saved to {JsonFile.Path}");
			}
			//else
			//{
			//	Debug.WriteLine($"{MemoryKey} NOT saved to {PitName}");
			//}

			// I think I had a version of the code where the destructor was called when the Pit was removed from the
			// cache because the time was up
			// Now, this does not work anymore, not in the debugger at least
			// I am trying to use https://msdn.microsoft.com/en-us/library/system.runtime.caching.cacheentrychangemonitor(v=vs.110).aspx
			// to store the Pit's changes in memory as soon as the current Cache want's to throw it out
		}
	}
	/// <summary>
	/// /// enables advanced synchronization via modified
	/// </summary>
	public class Item : ICloneable
	{
		//[JilDirective(Ignore = true)]
		/// <summary>Identifying name</summary>
		public string Name { get; set; }
		/// <summary>has to be set to DateTimeOffset.UtcNow explicitely</summary>
		public DateTimeOffset Modified { get; set; }
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
					Modified = DateTimeOffset.UtcNow - new TimeSpan(0, 0, 0, 100);  // backdate delete for 100ms
				Invalidate(preserveTimestamp: backDate100); // changes modified
				var s = $"[{Modified.ToUniversalTime().ToString("u")}] deleted";
				if (!string.IsNullOrEmpty(by))
					s += " by " + by;
				Note = s + ";\n" + Note;
			}
			return true;
		}
		/// <summary>
		/// means: XmlSetting was modified from the original state of the setting as it was once loaded from disk; 
		/// in a concurrent environment this does not necessarily mean that the current setting on disk has (still) an older value
		/// since the file could have been updated on any other machine and synchronized back to this machine.
		/// Use merge to get the youngest value - merge also adjusts the dirty flag accordingly.
		/// </summary>
		protected bool Dirty { get; set; }  // protected makes it non-persistent
		virtual public bool Valid() { return !Dirty; }
		/// <summary>call to indicate that the memory representation of the XmlSettings&lt;T&gt; now equals the file representation</summary>
		virtual public void Validate() { Dirty = false; }
		/// <summary>call to indicate that the memory representation of the XmlSettings&lt;T&gt; differs from the file representation</summary>
		/// <param name="preserveTimestamp">does not update modified (only sets the dirty flag) if true</param>
		virtual public void Invalidate(bool preserveTimestamp = false)
		{
			Dirty = true;
			if (!preserveTimestamp)
				Modified = DateTimeOffset.UtcNow;
		}
		public string Note { get; set; }
		/// <summary>
		/// also called by the debugger but no breakpoints will be hit by this calls
		/// </summary>
		/// <returns></returns>
		public override string ToString()
		{
			//var ser = new JavaScriptSerializer();	// Newtonsoft
			//var result = ser.Serialize(this);
			//return result;
			return JSON.Serialize<Item>(this);      // Jil
		}
		/// <summary>
		/// compare method - overload this when more complex comparison is wanted
		/// </summary>
		/// <param name="x"></param>
		/// <returns>true, if it matches</returns>
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
		/// <summary>
		/// Create a Clone of the this object (created via Serialization/Deserialization using Jil) - type is known
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <returns>a copy of the this object</returns>
		public T Clone<T>()
		{
			var s = JSON.SerializeDynamic(this, JsonPitBase.jilOptions);
			return JSON.Deserialize<T>(s, JsonPitBase.jilOptions);
		}
		public virtual dynamic Clone()
		{   // the current version of ServiceStack's JsonSerializer.DeserializeFromString puts null into Name (!!) => back to JavaScriptSerializer
			//var ser = new JavaScriptSerializer();
			//var s = ser.Serialize(this);
			////string s = JsonSerializer.SerializeToString(this, GetType());	// now using serializer from ServiceStack.Text
			//var o = ser.Deserialize(s, GetType());
			////object o = JsonSerializer.DeserializeFromString(s, GetType());
			// try it with Jil
			var s = JSON.SerializeDynamic(this, JsonPitBase.jilOptions);
			dynamic o;
			if (this.GetType().FullName.Contains("Dynamic"))
				o = JSON.DeserializeDynamic(s, JsonPitBase.jilOptions); // Jil
			else
			{
				//var ser = new JavaScriptSerializer();
				//o = ServiceStack.Text.JsonSerializer.DeserializeFromString(s, GetType());   // TODO: check if Name is still null and if yes, use NewtonSoft here
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
		/// <summary>
		/// merges a second setting into this setting; overload in derived classes; updates the dirty flag of this setting if second is younger (greater)
		/// </summary>
		/// <param name="second"></param>
		/// <remarks>this.dirty will be true after the call if second.dirty was true before the call AND second was modified more recently than this</remarks>
		public virtual void Merge(Item second)
		{
			// Name is identifying and must be the same
			if (Name != second.Name)
				throw new ArgumentException("Error: " + Name + ".Merge(" + second.Name + ") is an invalid call - Names must be equal.");
			if (Changed().UtcTicks == second.Changed().UtcTicks)    // was ==
			{
				Dirty = false;  // identical means memory is up to date
				return;
			}
			if (Changed().UtcTicks <= second.Changed().UtcTicks)        // less means older => use all the property values from the second XmlSetting; RSB 20150603: <= enables repeated reading of change files
			{
				Dirty = true; // FIX 20150602; RSB // !second.Valid();
				Modified = second.Modified;
				#region special treatment if second was just deleted
				if (second.Deleted) // special treatment
				{
					Dirty = Dirty || Deleted != second.Deleted; // if memory setting became deleted, the flag has to be set to cause writing a change file on the calling level
					Deleted = true;
				}
				else Deleted = false;
				// examples: if deleted flags don't match the dirty flag has to be set; otherwise this.dirty becomes second.dirty
				// !second.deleted && !this.deleted
				// second.deleted && !this.deleted => dirty
				// second.deleted && this.deleted && !second.dirty => not dirty
				// second.deleted && this.deleted && second.dirty => dirty
				#endregion
				#region set all properties to values of the second object's properties
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
							// second does not have this property: use this
							try
							{
								value = propertyInfo.GetValue(this, null);
								propertyInfo.SetValue(this, value, null);
							}
							catch (TargetParameterCountException)
							{
								//value = new Object();
								// init not possible
							}
						}
					}
				}
				#endregion
			}
			else Dirty = true;  // means: this has the younger version, second is old => next JsonFile.Store() fixes this
								// the rest has to be done in the derived classes ... maybe not - TODO check if the reflection solution above works
								// currently the following behavior shows: Modified and second.Modified are never the same; therefore, each run creates a change file for every setting that is
								// supposedly up to date but has a different Modified anyway.
		}
		/// <summary>Constructor</summary>
		/// <param name="name"></param>
		/// <param name="comment"></param>
		/// <param name="invalidate"></param>
		/// <remarks>timestamp of this setting will be set to UtcNow after this</remarks>
		public Item(string name, string comment, bool invalidate = true)
		{
			this.Name = name;
			Note = comment;
			if (invalidate)
				Invalidate();
		}
		/// <summary>copy-constructor</summary>
		/// <remarks>timestamp will be set to from's timestamp after this</remarks>
		public Item(Item from)
		{
			var clone = from.Clone();   // I might need a deep copy here (i.e. to have copies, not references of container contents)
			#region set all properties to values of the from object's properties
			foreach (var propertyInfo in this.GetType().GetProperties())
				if (propertyInfo.CanWrite)
					propertyInfo.SetValue(this, propertyInfo.GetValue(clone, null), null);
			#endregion
			Modified = from.Changed();  // setting the properties altered the timestamp; re-set it to from's 
		}
		/// <summary>Parameterless constructor</summary>
		/// <remarks>nothing will be set after this; leave everything (name, note and modified) for the serializer to set.
		/// This constructor is sort-of reserved for the use by the serializer; make sure to use any other constructor in your
		/// derived class, like :base(name, comment) to have the timestamp in modified initialized properly.
		/// If you want to use any other constructor as base class constructor for the parameterless constructor of your custom
		/// class XxxSetting, make sure to pass in invalidate: false. Otherwise merge would create erroneous results.
		/// see SubscriberSetting for an example
		/// </remarks>
		public Item()
		{
		}

		// public static explicit operator JObject(Item v)
		// {
		// 	return (JObject)v;	// just reinterpret it as JObject
		// }
		// public static explicit operator Item(JObject v)
		// {
		// 	return (Item)v;	// just reinterpret it as Item
		// }
		//// Error CS1964	'JsonItem.implicit operator JsonItem(dynamic)': user-defined conversions to or from the dynamic type are not allowed 
		////public static implicit operator JsonItem(dynamic d)
		////{
		////	return new JsonItem()
		////	{
		////		//..
		////	};
		////}
		///// <summary>
		///// Since this constructor creates a copy, the dynamic fields and properties will not be copied along the 
		///// way. If this object will be stored later, all dynamic field will get lost => TODO
		///// </summary>
		///// <param name="from"></param>
		//public JsonItem(dynamic from)
		//{
		//	name = from.name;
		//	note = from.note;
		//	dirty = false; //from.dirty;	TODO check if dirty exists; it does not exist yet if the object came straight from file; otherwise it should.
		//	modified = from.modified;
		//	deleted = from.deleted;
		//}
	}
}
