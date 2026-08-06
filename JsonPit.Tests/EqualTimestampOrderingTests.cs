using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

/// <summary>
/// CR003 §3 — agreed v3.13.2 equal-timestamp and replay behavior. Pure in-memory
/// history mechanics of <see cref="PitItems"/>: no filesystem is involved, so no cloud
/// root is required or substituted.
/// </summary>
public sealed class EqualTimestampOrderingTests
{
	private static PitItem Fragment(string id, DateTimeOffset modified, params (string Name, JToken Value)[] properties)
	{
		var json = new JObject
		{
			[nameof(PitItem.Id)] = id,
			[nameof(PitItem.Modified)] = modified,
			[nameof(PitItem.Deleted)] = false
		};
		foreach (var (name, value) in properties)
			json[name] = value;
		return new PitItem(json);
	}

	[Fact]
	public void ExactReplay_IsIdempotent_AndDoesNotConsumeABoundedHistorySlot()
	{
		var t = DateTimeOffset.UtcNow;
		var a = Fragment("X", t.AddMinutes(-2), ("P", 1));
		var b = Fragment("X", t.AddMinutes(-1), ("P", 2));
		var c = Fragment("X", t, ("P", 3));
		var history = PitItems.Create("X", maxCount: 3).Push(a).Push(b).Push(c);
		Assert.Equal(3, history.Count);

		// Replaying an exact existing fragment is ignored — bounded-history protection:
		// the oldest real fragment must not be evicted by a replay duplicate.
		var replayed = history.Push(Fragment("X", t.AddMinutes(-1), ("P", 2)));
		Assert.Same(history, replayed);
		Assert.Equal(3, replayed.Count);
		Assert.Contains(replayed.History, f => (int)f["P"] == 1);
	}

	[Fact]
	public void EqualTimestamps_DistinctFragments_AreBothRetained_NotAnException()
	{
		var t = DateTimeOffset.UtcNow;
		var small = Fragment("X", t, ("P1", "small"));
		var large = Fragment("X", t, ("P1", "large"), ("P2", "extra"));
		var history = PitItems.Create("X").Push(large).Push(small);
		Assert.Equal(2, history.Count);
	}

	[Fact]
	public void EqualTimestamps_FewerPropertiesSortFirst_AndHaveProjectionPrecedence()
	{
		var t = DateTimeOffset.UtcNow;
		// pi0 has p1..p10; pi1 (same timestamp) has p1..p11 with a contradictory p10.
		var pi0 = Fragment("X", t, Enumerable.Range(1, 10).Select(i => ($"p{i}", (JToken)$"pi0-{i}")).ToArray());
		var pi1 = Fragment("X", t, Enumerable.Range(1, 11).Select(i => ($"p{i}", (JToken)$"pi1-{i}")).ToArray());

		foreach (var arrival in new[] { new[] { pi0, pi1 }, new[] { pi1, pi0 } })
		{
			var history = PitItems.Create("X");
			foreach (var fragment in arrival)
				history = history.Push(new PitItem(fragment));
			// The smaller fragment sorts first and wins the contradictory overlap …
			Assert.Equal("pi0-10", (string)history.ProjectState()["p10"]);
			// … while the later equal-time fragment still contributes its new property.
			Assert.Equal("pi1-11", (string)history.ProjectState()["p11"]);
		}
	}

	[Fact]
	public void EqualTimestampAndPropertyCount_CanonicalContentSuppliesDeterministicOrdinalTieBreak()
	{
		var t = DateTimeOffset.UtcNow;
		var one = Fragment("X", t, ("P", "aaa"));
		var two = Fragment("X", t, ("P", "bbb"));

		var forward = PitItems.Create("X").Push(new PitItem(one)).Push(new PitItem(two));
		var backward = PitItems.Create("X").Push(new PitItem(two)).Push(new PitItem(one));

		Assert.Equal(
			forward.History.Select(f => CanonicalJson.Canonicalize(f)).ToList(),
			backward.History.Select(f => CanonicalJson.Canonicalize(f)).ToList());
		Assert.Equal(
			CanonicalJson.Canonicalize(forward.ProjectState()),
			CanonicalJson.Canonicalize(backward.ProjectState()));
	}

	[Fact]
	public void Projection_IsIdenticalRegardlessOfFragmentArrivalOrder()
	{
		var t = DateTimeOffset.UtcNow;
		var fragments = new[]
		{
			Fragment("X", t.AddMinutes(-2), ("A", 1), ("B", 1)),
			Fragment("X", t, ("A", 2)),
			Fragment("X", t, ("A", 3), ("C", 3)),
			Fragment("X", t.AddMinutes(-1), ("B", 4))
		};

		string reference = null;
		foreach (var permutation in Permutations(fragments))
		{
			var history = PitItems.Create("X");
			foreach (var fragment in permutation)
				history = history.Push(new PitItem(fragment));
			var projected = CanonicalJson.Canonicalize(history.ProjectState());
			reference ??= projected;
			Assert.Equal(reference, projected);
		}
	}

	private static System.Collections.Generic.IEnumerable<PitItem[]> Permutations(PitItem[] items)
	{
		if (items.Length <= 1)
		{
			yield return items;
			yield break;
		}
		foreach (var (item, index) in items.Select((item, index) => (item, index)))
			foreach (var rest in Permutations(items.Where((_, i) => i != index).ToArray()))
				yield return new[] { item }.Concat(rest).ToArray();
	}
}
