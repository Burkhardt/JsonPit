using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace JsonPit;

public partial class Pit
{
	private static int defaultMutationTrackingMode = (int)MutationTrackingMode.TrackedChangesWithFallback;
	/// <summary>
	/// Process-local default for subsequently constructed Pit instances. Set at server
	/// startup before opening pits. Existing instances and persisted files are unaffected.
	/// </summary>
	public static MutationTrackingMode DefaultMutationTrackingMode
	{
		get => (MutationTrackingMode)Volatile.Read(ref defaultMutationTrackingMode);
		set
		{
			if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
			Volatile.Write(ref defaultMutationTrackingMode, (int)value);
		}
	}
	/// <summary>The immutable policy captured for this instance's lifetime in memory.</summary>
	public MutationTrackingMode TrackingMode { get; } = DefaultMutationTrackingMode;

	private sealed class LiveSlot
	{
		internal readonly object Gate = new();
		internal volatile LivePitItem Current;
	}
	private ConcurrentDictionary<string, LiveSlot> liveItems = new(StringComparer.InvariantCulture);

	internal void EnsureLiveOwner(LivePitItem live)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		live.EnsureActive();
		if (!liveItems.TryGetValue(live.Id, out var slot) || !ReferenceEquals(slot.Current, live))
			throw new InvalidOperationException($"Item '{live.Id}' is not the current live item of this Pit.");
		if (live.Item.Deleted) throw new InvalidOperationException($"Item '{live.Id}' has been deleted.");
	}

	private bool WithLiveState(string id, Func<LiveSlot, bool> action)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		var enter = !stateGate.IsReadLockHeld && !stateGate.IsWriteLockHeld;
		if (enter) stateGate.EnterReadLock();
		try
		{
			var slot = liveItems.GetOrAdd(id, _ => new LiveSlot());
			lock (slot.Gate) return action(slot);
		}
		finally { if (enter) stateGate.ExitReadLock(); }
	}

	internal bool AcceptLiveChanges(LivePitItem live) => WithLiveState(live.Id, slot =>
	{
		try
		{
			EnsureLiveOwner(live);
			return FlushLiveItem(slot);
		}
		catch
		{
			live.Restore();
			throw;
		}
	});

	private bool FlushLiveItem(LiveSlot slot)
	{
		var live = slot.Current;
		if (live is null || live.IsRefreshing) return false;
		try
		{
			var changes = live.Changes();
			if (!changes.HasValues) return false;
			EnsureLiveOwner(live);
			return AppendFragment(slot, PitItem.CreateSparseMutation(live.Id, changes), refreshModified: true);
		}
		catch
		{
			live.Restore();
			throw;
		}
	}

	private void FlushLiveItems()
	{
		if (TrackingMode != MutationTrackingMode.TrackedChangesWithFallback) return;
		foreach (var pair in liveItems)
			WithLiveState(pair.Key, FlushLiveItem);
	}
	private bool FlushFallback(LiveSlot slot) =>
		TrackingMode == MutationTrackingMode.TrackedChangesWithFallback && FlushLiveItem(slot);

	internal bool ApplyLivePatch(LivePitItem live, JObject patch)
	{
		PitItem.ValidatePropertyMutationPayload(patch);
		return WithLiveState(live.Id, slot =>
		{
			EnsureLiveOwner(live);
			FlushFallback(slot);
			var projected = (JObject)live.Baseline.DeepClone();
			foreach (var property in patch.Properties())
				PitItems.ApplyProjectedProperty(projected, property.Name, property.Value);
			if (JToken.DeepEquals(projected, live.Baseline)) return false;
			return AppendFragment(slot, PitItem.CreateSparseMutation(live.Id, patch), refreshModified: true);
		});
	}

	internal bool DeleteLiveItem(LivePitItem live, string by, bool backDate)
	{
		if (live.Item.Deleted) return false;
		EnsureLiveOwner(live);
		return Delete(live.Id, by, backDate);
	}

	/// <summary>Called under the state gate and this identity's gate, never from an accessor.</summary>
	private bool AppendFragment(LiveSlot slot, PitItem item, bool refreshModified, bool legacyTombstone = false)
	{
		if (refreshModified && !legacyTombstone) item.EnsureValidForLiveAdd();
		var stamped = false;
		while (true)
		{
			var current = HistoricItems.GetOrAdd(item.Id, key => PitItems.Create(key, DefaultMaxCount));
			if (current.LatestFragment() is { } top && EqualsIgnoringModified(top, item)) return false;
			if (refreshModified && !stamped)
			{
				item.Invalidate(NextLiveMutationTimestamp());
				stamped = true;
			}
			var updated = current.Push(item);
			if (ReferenceEquals(updated, current)) return false;
			if (!HistoricItems.TryUpdate(item.Id, updated, current)) continue;
			var adopt = refreshModified && (current.Count == 0 || current.LatestFragment().Deleted);
			PublishLiveState(slot, item.Id, updated, adopt ? item : null);
			return true;
		}
	}

	private void PublishLiveState(LiveSlot slot, string id, PitItems history, PitItem original = null)
	{
		var projected = history?.ProjectState(withDeleted: true);
		if (projected is null)
		{
			slot.Current?.Retire();
			slot.Current = null;
			return;
		}
		if (slot.Current is null || slot.Current.Item.Deleted && !projected.Deleted)
		{
			slot.Current?.Retire();
			slot.Current = new LivePitItem(this, id, original ?? projected, projected);
		}
		else slot.Current.Refresh(projected);
	}

	private void PublishAllLiveState()
	{
		foreach (var id in HistoricItems.Keys.Concat(liveItems.Keys).Distinct(Comparer))
		{
			var slot = liveItems.GetOrAdd(id, _ => new LiveSlot());
			lock (slot.Gate)
				PublishLiveState(slot, id, HistoricItems.TryGetValue(id, out var history) ? history : null);
		}
	}
}
