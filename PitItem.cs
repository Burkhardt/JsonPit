using Jil;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
namespace JsonPit;
public enum Compare { JSON, ByProperty }
/// <summary>
/// JSON-backed item with metadata and change tracking.
/// Extends JObject for native JSON merge/query support.
/// </summary>
public class PitItem : JObject, IEquatable<PitItem>
{
	public string Id
	{
		get => (string)this[nameof(Id)];
		set => this[nameof(Id)] = value;
	}
	public DateTimeOffset Modified
	{
		get => (DateTimeOffset)this[nameof(Modified)];
		internal set => this[nameof(Modified)] = value.ToUniversalTime();
	}
	public bool Deleted
	{
		get
		{
			var q = from _ in Properties() where _.Name == "Deleted" select _;
			if (!q.Any())
				throw new KeyNotFoundException($"Deleted does not exist in Item {Id}");
			return (bool)this[nameof(Deleted)];
		}
		set => this[nameof(Deleted)] = value;
	}
	public string Note
	{
		get => (string)this[nameof(Note)];
		set => this[nameof(Note)] = value;
	}
	#region Mutation
	public bool SetProperty(string objectAsJsonString) => ExtendWith(JObject.Parse(objectAsJsonString));
	public void SetProperty(object obj) => SetProperty(JSON.SerializeDynamic(obj));
	public void DeleteProperty(string propertyName)
	{
		Deleted = false;
		Invalidate();
		this[propertyName] = null;
	}
	public bool Delete(string by = null, bool backDate100 = true)
	{
		if (Deleted) return false;
		Deleted = true;
		if (backDate100)
			Modified = DateTimeOffset.UtcNow - new TimeSpan(0, 0, 0, 100);
		Invalidate();
		var s = $"[{Modified.ToUniversalTime():u}] deleted";
		if (!string.IsNullOrEmpty(by)) s += " by " + by;
		Note = s + ";\n" + Note;
		return true;
	}
	#endregion
	#region Dirty tracking
	protected bool Dirty { get; set; }
	public virtual bool Valid() => !Dirty;
	public virtual void Validate() => Dirty = false;
	public virtual void Invalidate()
	{
		Dirty = true;
		Modified = DateTimeOffset.UtcNow;
	}
	#endregion
	public override string ToString()
	{
		var settings = new JsonSerializerSettings { DateTimeZoneHandling = DateTimeZoneHandling.Utc };
		return JsonConvert.SerializeObject(this, settings);
	}
	#region IEquatable<PitItem>
	public bool Equals(PitItem other)
	{
		if (other is null) return false;
		if (Id != other.Id || Modified.UtcTicks != other.Modified.UtcTicks) return false;
		return ToString() == other.ToString();
	}
	public override bool Equals(object obj) => obj is PitItem other ? Equals(other) : base.Equals(obj);
	public override int GetHashCode() => HashCode.Combine(Id, Modified.UtcTicks);
	#endregion
	#region Extend / Merge
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
		var originalClone = (JObject)DeepClone();
		var mergeSettings = new JsonMergeSettings
		{
			MergeArrayHandling = MergeArrayHandling.Replace,
			MergeNullValueHandling = MergeNullValueHandling.Ignore
		};
		Merge(obj.DeepClone(), mergeSettings);
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
			else if (!JToken.DeepEquals(this["_"], el))
			{
				this["_"] = el;
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
	#endregion
	#region Constructors
	public PitItem(string id, bool invalidate = true, string comment = "")
	{
		Id = id;
		Note = comment;
		if (invalidate) Invalidate();
		Deleted = false;
	}
	public PitItem(string id, object extendWith, string comment = "")
		: this(id, JSON.SerializeDynamic(extendWith), comment) { }
	public PitItem(string id, string extendWithAsJson, string comment = "")
	{
		Id = id;
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
		Modified = timestamp ?? (DateTimeOffset)other[nameof(Modified)];
	}
	public PitItem(JObject from) : base((JObject)from.DeepClone())
	{
		if (this[nameof(Id)] is null && this["Name"] is JValue nameToken && nameToken.Type == JTokenType.String)
		{
			Id = nameToken.Value<string>();
		}
		try { Deleted = (bool)this[nameof(Deleted)]; }
		catch (Exception ex)
		{
			Deleted = false;
			if (Deleted) Console.WriteLine(ex);
		}
		Dirty = true;
		try { Modified = (DateTimeOffset)this[nameof(Modified)]; }
		catch (Exception) { Modified = DateTimeOffset.UtcNow; }
		Id = (string)this[nameof(Id)];
		if (Property(nameof(Note)) is not null)
			Note = (string)this[nameof(Note)];
	}
	public PitItem() { }
	#endregion
}
