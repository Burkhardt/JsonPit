using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace JsonPit;

/// <summary>
/// Attaches to the original JSON graph. The baseline is the last accepted state,
/// not another live entity. It detects edits that Newtonsoft does not notify.
/// </summary>
internal sealed class LivePitItem
{
	private readonly WeakReference<Pit> owner;
	private readonly HashSet<JContainer> observed = new(ReferenceEqualityComparer.Instance);
	private readonly HashSet<JObject> changingProperties = new(ReferenceEqualityComparer.Instance);
	private int refreshing;
	private bool retired;
	internal string Id { get; }
	internal PitItem Item { get; }
	internal JObject Baseline { get; private set; }
	internal bool IsRefreshing => refreshing != 0;
	internal Pit Owner => owner.TryGetTarget(out var pit) ? pit : throw new ObjectDisposedException(nameof(Pit));

	internal LivePitItem(Pit pit, string id, PitItem item, PitItem projected)
	{
		owner = new WeakReference<Pit>(pit);
		Id = id;
		Item = item;
		Item.LiveBinding = this;
		Refresh(projected);
	}

	internal bool ApplyPatch(JObject patch) => Owner.ApplyLivePatch(this, patch);
	internal bool Delete(string by, bool backDate) => Owner.DeleteLiveItem(this, by, backDate);

	internal void EnsureActive()
	{
		if (retired) throw new InvalidOperationException($"The live item '{Id}' is no longer attached to this Pit.");
	}

	internal void Refresh(JObject projected)
	{
		refreshing++;
		try
		{
			Reconcile(Item, projected);
			Baseline = (JObject)projected.DeepClone();
			ObserveGraph();
		}
		finally { refreshing--; }
	}

	internal void Restore() => Refresh(Baseline);

	internal void Retire()
	{
		retired = true;
		// Keep the root guard so a retained PitItem cannot silently write to a new
		// lifetime. Detached descendants are ordinary JSON nodes, without an owner.
		foreach (var node in observed.Where(node => !ReferenceEquals(node, Item)).ToArray())
			Unobserve(node);
	}

	private void ObserveGraph()
	{
		var nodes = Item.DescendantsAndSelf().OfType<JContainer>()
			.Where(node => node is JObject or JArray)
			.ToHashSet<JContainer>(ReferenceEqualityComparer.Instance);
		foreach (var node in observed.Where(node => !nodes.Contains(node)).ToArray()) Unobserve(node);
		foreach (var node in nodes)
		{
			if (!observed.Add(node)) continue;
			// Keep a root subscriber too: Newtonsoft calls OnCollectionChanged only
			// when it has subscribers. PitItem dispatches after that notification ends.
			((INotifyCollectionChanged)node).CollectionChanged += Changed;
			if (node is JObject obj)
			{
				obj.PropertyChanging += Changing;
				if (!ReferenceEquals(node, Item)) obj.PropertyChanged += PropertyChanged;
			}
		}
	}

	private void Unobserve(JContainer node)
	{
		((INotifyCollectionChanged)node).CollectionChanged -= Changed;
		if (node is JObject obj)
		{
			obj.PropertyChanging -= Changing;
			obj.PropertyChanged -= PropertyChanged;
			changingProperties.Remove(obj);
		}
		observed.Remove(node);
	}

	private void Changing(object sender, PropertyChangingEventArgs args)
	{
		if (IsRefreshing) return;
		EnsureActive();
		Owner.EnsureLiveOwner(this);
		if (ReferenceEquals(sender, Item) && IsProtected(args.PropertyName))
			throw new ProtectedAttributeException(args.PropertyName);
		if (sender is JObject obj && !ReferenceEquals(obj, Item)) changingProperties.Add(obj);
	}

	private void Changed(object sender, NotifyCollectionChangedEventArgs args)
	{
		if (IsRefreshing || ReferenceEquals(sender, Item)) return;
		// A new indexer property raises CollectionChanged inside Add before its
		// PropertyChanged notification. Wait for that notification before pruning
		// tombstones, so we never edit the collection inside its own notification.
		if (sender is JObject obj && changingProperties.Contains(obj)) return;
		Owner.AcceptLiveChanges(this);
	}
	private void PropertyChanged(object sender, PropertyChangedEventArgs args)
	{
		if (sender is JObject obj) changingProperties.Remove(obj);
		RootChanged();
	}
	internal void RootChanged()
	{
		if (!IsRefreshing) Owner.AcceptLiveChanges(this);
	}

	internal static bool IsProtected(string name) =>
		string.Equals(name, nameof(PitItem.Id), StringComparison.OrdinalIgnoreCase) ||
		string.Equals(name, nameof(PitItem.Modified), StringComparison.OrdinalIgnoreCase) ||
		string.Equals(name, nameof(PitItem.Deleted), StringComparison.OrdinalIgnoreCase);

	internal JObject Changes()
	{
		var changes = Difference(Baseline, Item);
		PitItem.ValidatePropertyMutationPayload(changes);
		return changes;
	}

	/// <summary>Objects contribute leaf deltas; arrays remain atomic JSON values.</summary>
	internal static JObject Difference(JObject before, JObject after)
	{
		var changes = new JObject();
		foreach (var name in before.Properties().Select(p => p.Name)
			.Concat(after.Properties().Select(p => p.Name)).Distinct(StringComparer.Ordinal))
		{
			var oldValue = before[name];
			var newValue = after[name];
			if (JToken.DeepEquals(oldValue, newValue)) continue;
			if (oldValue is JObject oldObject && newValue is JObject newObject)
			{
				var nested = Difference(oldObject, newObject);
				if (nested.HasValues) changes[name] = nested;
			}
			else changes[name] = newValue?.DeepClone() ?? JValue.CreateNull();
		}
		return changes;
	}

	/// <summary>Preserve object identities and unchanged array identities while establishing accepted values.</summary>
	private static void Reconcile(JObject target, JObject source)
	{
		foreach (var property in target.Properties().Where(p => source.Property(p.Name) is null).ToArray())
			property.Remove();
		foreach (var property in source.Properties())
		{
			var current = target[property.Name];
			if (JToken.DeepEquals(current, property.Value)) continue;
			if (current is JObject obj && property.Value is JObject incoming) Reconcile(obj, incoming);
			else target[property.Name] = property.Value.DeepClone();
		}
	}

}
