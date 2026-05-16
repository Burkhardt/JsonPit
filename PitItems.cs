using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
namespace JsonPit;
/// <summary>
/// Base container holding a key identifier for item groups.
/// </summary>
public class ItemsBase(string key = null)
{
	/// <summary>Identifying id, i.e. JsonFileName from enclosing JsonFile</summary>
	public string Key = key;
}
/// <summary>
/// History stack of PitItem versions for a single key.
/// Immutable — Push returns a new instance, enabling lock-free CAS updates on ConcurrentDictionary.
/// Stack semantics: <c>History[0]</c> is the newest fragment, <c>History[^1]</c> the oldest.
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
		new(key, ImmutableList<PitItem>.Empty, maxCount);
	public PitItems Push(PitItem item)
	{
		// Newest first: descending by Modified.  History[0] is the most
		// recently modified fragment; History[^1] is the oldest.
		var newHistory = History.Add(item)
			.Sort((a, b) => b.Modified.CompareTo(a.Modified));
		if (MaxCount > 0 && newHistory.Count > MaxCount)
			newHistory = newHistory.RemoveRange(MaxCount, newHistory.Count - MaxCount);
		return new PitItems(Key, newHistory, MaxCount);
	}
	internal PitItem LatestFragment() => History.IsEmpty ? null : History[0];
	private int FindProjectionStartIndex(DateTimeOffset? at)
	{
		if (History.IsEmpty) return -1;
		if (at is null) return 0;
		for (int i = 0; i < History.Count; i++)
			if (History[i].Modified <= at.Value)
				return i;
		return -1;
	}
	public PitItem ProjectState(DateTimeOffset? at = null, bool withDeleted = false)
	{
		var startIndex = FindProjectionStartIndex(at);
		if (startIndex < 0) return null;
		var newest = History[startIndex];
		if (newest.Deleted)
		{
			if (!withDeleted) return null;
			return new PitItem(new JObject
			{
				[nameof(PitItem.Id)] = newest.Id,
				[nameof(PitItem.Modified)] = newest.Modified,
				[nameof(PitItem.Deleted)] = true
			});
		}
		var accumulator = new JObject();
		for (int i = startIndex; i < History.Count; i++)
		{
			var fragment = History[i];
			if (fragment.Deleted) break;
			foreach (var property in fragment.Properties())
				accumulator.TryAdd(property.Name, property.Value.DeepClone());
		}
		accumulator[nameof(PitItem.Id)] = newest.Id;
		accumulator[nameof(PitItem.Modified)] = newest.Modified;
		accumulator[nameof(PitItem.Deleted)] = false;
		return new PitItem(accumulator);
	}
	public PitItem Peek(DateTimeOffset? timestamp = null) => ProjectState(timestamp);
	public JObject Get(DateTimeOffset? timestamp = null) => ProjectState(timestamp);
	public int Count => History.Count;
	public IEnumerator<PitItem> GetEnumerator() => History.GetEnumerator();
	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	/// <summary>Compatibility constructor accepting any IEnumerable of PitItems.</summary>
	public PitItems(string key = null, IEnumerable<PitItem> value = null, int maxCount = 5) : base(key)
	{
		MaxCount = maxCount;
		Key = key;
		var list = ImmutableList<PitItem>.Empty;
		if (value is not null)
		{
			foreach (var v in value) list = list.Add(v);
			if (list.Count > 1) list = list.Sort((a, b) => b.Modified.CompareTo(a.Modified));
			Key = key ?? list.FirstOrDefault()?.Id;
		}
		History = list;
	}
}
