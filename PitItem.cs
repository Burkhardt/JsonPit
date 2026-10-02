using Jil;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
namespace JsonPit;
public enum Compare { JSON, ByProperty }
/// <summary>
/// JSON-backed item with metadata and change tracking.
/// Extends JObject for native JSON merge/query support.
/// </summary>
public class PitItem : JObject, IEquatable<PitItem>
{
	private static readonly string[] ProtectedMutationAttributes = [nameof(Id), nameof(Modified), nameof(Deleted)];
	private string ClientSuppliedLifecycleAttribute { get; set; }
	private bool inferredLegacyId;
	internal LivePitItem LiveBinding { get; set; }
	/// <summary>
	/// Observe the root after Newtonsoft finishes notifying other subscribers. A live
	/// refresh can then remove tombstoned properties without collection-event reentrancy.
	/// </summary>
	protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs args)
	{
		try { base.OnCollectionChanged(args); }
		finally { LiveBinding?.RootChanged(); }
	}

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
	public bool SetProperty(string objectAsJsonString) => ExtendWith(PitJson.ParseObject(objectAsJsonString));
	public void SetProperty(object obj) => SetProperty(JSON.SerializeDynamic(obj));
	public void DeleteProperty(string propertyName)
	{
		ThrowIfProtectedTombstone(propertyName);
		if (LiveBinding is { } live)
		{
			live.ApplyPatch(new JObject { [propertyName] = JValue.CreateNull() });
			return;
		}
		Deleted = false;
		Invalidate();
		this[propertyName] = null;
	}
	/// <summary>
	/// Appends a property tombstone at a dot-delimited nested JSON path.
	/// Unlike <see cref="DeleteProperty(string)"/>, dots are path separators here.
	/// </summary>
	public void DeletePropertyPath(string propertyPath)
	{
		var segments = ParsePropertyPath(propertyPath);
		ThrowIfProtectedTombstone(segments[0]);
		if (LiveBinding is { } live)
		{
			var patch = new JObject();
			var target = patch;
			JToken current = this;
			foreach (var segment in segments.Take(segments.Length - 1))
			{
				current = (current as JObject)?[segment];
				if (current is { Type: not JTokenType.Null } and not JObject)
					throw new ArgumentException($"Property path '{propertyPath}' cannot traverse non-object property '{segment}'.", nameof(propertyPath));
				var child = new JObject();
				target[segment] = child;
				target = child;
			}
			target[segments[^1]] = JValue.CreateNull();
			live.ApplyPatch(patch);
			return;
		}
		if (segments.Length == 1)
		{
			DeleteProperty(segments[0]);
			return;
		}

		JObject container = this;
		for (var index = 0; index < segments.Length - 1; index++)
		{
			var segment = segments[index];
			if (container[segment] is JObject nested)
			{
				container = nested;
				continue;
			}
			if (container[segment] is { Type: not JTokenType.Null })
				throw new ArgumentException(
					$"Property path '{propertyPath}' cannot traverse non-object property '{segment}'.",
					nameof(propertyPath));

			var created = new JObject();
			container[segment] = created;
			container = created;
		}

		container[segments[^1]] = JValue.CreateNull();
		Deleted = false;
		Invalidate();
	}
	public bool Delete(string by = null, bool backDate100 = true)
	{
		if (LiveBinding is { } live) return live.Delete(by, backDate100);
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
		=> Invalidate(DateTimeOffset.UtcNow);
	internal void Invalidate(DateTimeOffset modified)
	{
		Dirty = true;
		Modified = modified;
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
		var token = PitJson.Parse(json);
		return token switch
		{
			JObject obj => ExtendWith(obj),
			JArray arr => ExtendWith(arr),
			_ => false
		};
	}
	public virtual bool ExtendWith(JObject obj)
	{
		ValidatePropertyMutationPayload(obj);
		if (LiveBinding is { } live) return live.ApplyPatch(obj);
		var originalClone = (JObject)DeepClone();
		var mergeSettings = new JsonMergeSettings
		{
			MergeArrayHandling = MergeArrayHandling.Replace,
			MergeNullValueHandling = MergeNullValueHandling.Merge
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
	/// <summary>
	/// Merges a partial JSON object using JsonPit tombstone semantics. Null values
	/// are retained in the historic fragment and omitted when state is projected.
	/// </summary>
	public bool Merge(JObject patch)
	{
		if (patch is null)
			throw new ArgumentNullException(nameof(patch));
		return ExtendWith(patch);
	}
	public virtual bool ExtendWith(JArray arr)
	{
		foreach (var row in arr.OfType<JObject>())
			ValidatePropertyMutationPayload(row);
		if (LiveBinding is { } live)
		{
			var patch = new JObject();
			foreach (var el in arr)
				if (el is JObject row)
					foreach (var property in row.Properties()) patch[property.Name] = property.Value.DeepClone();
				else patch["_"] = el.DeepClone();
			return live.ApplyPatch(patch);
		}
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
	/// <summary>
	/// Validates a client entity payload before live ingestion. <c>Id</c> remains the
	/// required entity key, but lifecycle attributes are reserved for historical replay.
	/// </summary>
	public static void ValidateClientPayload(JObject payload)
	{
		if (payload is null) throw new ArgumentNullException(nameof(payload));
		ValidateLiveId(payload[nameof(Id)]);
		var protectedProperty = payload.Properties().FirstOrDefault(property =>
			string.Equals(property.Name, nameof(Modified), StringComparison.OrdinalIgnoreCase) ||
			string.Equals(property.Name, nameof(Deleted), StringComparison.OrdinalIgnoreCase));
		if (protectedProperty is not null)
			throw new ProtectedAttributeException(protectedProperty.Name);
	}

	internal void EnsureValidForLiveAdd()
	{
		if (inferredLegacyId) throw new ArgumentException("Entity Id must be a non-empty string.");
		ValidateLiveId(this[nameof(Id)]);
		if (!string.IsNullOrWhiteSpace(ClientSuppliedLifecycleAttribute))
			throw new ProtectedAttributeException(ClientSuppliedLifecycleAttribute);
	}

	/// <summary>Validates an entity identity at a live write boundary (CR049).</summary>
	public static void ValidateLiveId(string id) => ValidateLiveId(id is null ? null : new JValue(id));
	private static void ValidateLiveId(JToken id)
	{
		if (id?.Type != JTokenType.String || string.IsNullOrWhiteSpace(id.Value<string>()))
			throw new ArgumentException("Entity Id must be a non-empty string.");
		var value = id.Value<string>();
		if (value.Contains('{') || value.Contains('<'))
			throw new ArgumentException($"Entity Id '{value}' contains a prohibited template marker ('{{' or '<'). Resolve template placeholders before writing to a Pit.");
	}

	internal static void ValidatePropertyMutationPayload(JObject payload)
	{
		if (payload is null) throw new ArgumentNullException(nameof(payload));
		var protectedProperty = payload.Properties().FirstOrDefault(property =>
			ProtectedMutationAttributes.Any(attribute =>
				string.Equals(attribute, property.Name, StringComparison.OrdinalIgnoreCase)));
		if (protectedProperty is not null)
			throw new ProtectedAttributeException(protectedProperty.Name);
	}

	private static void ThrowIfProtectedTombstone(string propertyName)
	{
		var protectedAttribute = ProtectedMutationAttributes.FirstOrDefault(attribute =>
			string.Equals(attribute, propertyName, StringComparison.OrdinalIgnoreCase));
		if (protectedAttribute is not null)
			throw new TombstoneException(protectedAttribute);
	}

	private static string[] ParsePropertyPath(string propertyPath)
	{
		if (string.IsNullOrWhiteSpace(propertyPath))
			throw new ArgumentException("A property path is required.", nameof(propertyPath));

		var segments = propertyPath.Split('.', StringSplitOptions.None);
		if (segments.Any(string.IsNullOrWhiteSpace))
			throw new ArgumentException(
				"A property path must contain non-empty dot-delimited property names.",
				nameof(propertyPath));
		return segments;
	}
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
		ClientSuppliedLifecycleAttribute = nameof(Modified);
		inferredLegacyId = other.inferredLegacyId;
	}
	public PitItem(JObject from) : this(from, captureClientLifecycleAttributes: true) { }

	private PitItem(JObject from, bool captureClientLifecycleAttributes) : base((JObject)from.DeepClone())
	{
		if (captureClientLifecycleAttributes)
		{
			ClientSuppliedLifecycleAttribute = from.Properties()
				.FirstOrDefault(property =>
					string.Equals(property.Name, nameof(Modified), StringComparison.OrdinalIgnoreCase) ||
					string.Equals(property.Name, nameof(Deleted), StringComparison.OrdinalIgnoreCase))
				?.Name;
		}
		// Historical Name-only payloads remain readable, but cannot enter live writes.
		if (this[nameof(Id)] is null && this["Name"] is JValue { Type: JTokenType.String } nameToken)
		{
			Id = nameToken.Value<string>();
			inferredLegacyId = true;
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
		if (Property(nameof(Note)) is not null)
			Note = (string)this[nameof(Note)];
	}

	internal static PitItem CreateEngineMutationCopy(PitItem source, string id)
	{
		if (source is null) throw new ArgumentNullException(nameof(source));
		var result = new PitItem((JObject)source.DeepClone(), captureClientLifecycleAttributes: false)
		{
			Id = id
		};
		return result;
	}
	/// <summary>Owns historical bytes without retaining a live binding or changing dirty/provenance metadata.</summary>
	internal PitItem Snapshot() => new(this)
	{
		Dirty = Dirty,
		ClientSuppliedLifecycleAttribute = ClientSuppliedLifecycleAttribute,
		inferredLegacyId = inferredLegacyId
	};
	internal static PitItem CreateSparseMutation(string id, JObject patch)
	{
		ValidatePropertyMutationPayload(patch);
		var payload = (JObject)patch.DeepClone();
		payload[nameof(Id)] = id;
		return new PitItem(payload, captureClientLifecycleAttributes: false);
	}
	public PitItem() { }
	#endregion
}
