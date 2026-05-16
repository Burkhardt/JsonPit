using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using OsLib;
using Xunit;
using JsonPit;

namespace JsonPit.Tests;

/// <summary>
/// Strict, isolated unit tests for the <em>stale-timestamp</em> hypothesis in JsonPit.
///
/// These tests deliberately avoid any ASP.NET / HTTP / file-system test harness:
/// they exercise <see cref="Pit"/> and <see cref="PitItem"/> directly in-process.
///
/// Hypothesis under test (from RSB):
///   "Some method inside JsonPit uses an already-initialized (or copied)
///    <c>Modified</c> attribute when it is instead supposed to create a new
///    timestamp using <c>UtcNow</c> and set <c>Modified</c> with it."
///
/// Concretely, the symptom in the wild is that when a history is being compiled
/// from upstream PitItems (each carrying its own pre-existing <c>Modified</c>),
/// the resulting in-memory history shows duplicated <em>old</em> timestamps —
/// i.e. distinct payloads end up sharing a previously-recorded <c>Modified</c>
/// instead of each receiving a fresh <c>Now</c> stamp at insertion time.
///
/// Decisive assertion: after <c>Pit.Add(item)</c> returns true, the
/// <c>Modified</c> of the stored fragment must be &ge; the wall-clock instant
/// captured immediately before the call — regardless of what <c>item.Modified</c>
/// was at the time of the call.
/// </summary>
public class PitAddTimestampStalenessTests
{
	private const string ItemId = "staleness-probe";

	private static Pit NewPit(string label)
	{
		var dir = RAIkeepTestEnvironment.CloudPath("PitAddStaleness", label + "-" + Guid.NewGuid().ToString("N"));
		dir.mkdir();
		return new Pit(dir, readOnly: false, autoload: false);
	}

	private static IReadOnlyList<DateTimeOffset> HistoryTimestamps(Pit pit, string id)
	{
		// ValuesOverTime is the public, history-aware projection surface.
		// We pick an arbitrary property name; only the timestamps matter here.
		return pit.ValuesOverTime(id, "Payload")
			.Select(kvp => kvp.Key)
			.ToList();
	}

	// =====================================================================
	// 1. THE DECISIVE BUG-PROOF TEST
	//    Pit.Add of an item carrying an OLD Modified must result in a
	//    stored fragment whose Modified is fresh (>= the call instant),
	//    NOT the inherited old value.
	// =====================================================================
	[Fact]
	public void Pit_Add_MustStampFreshModified_NotInheritOldOneFromIncomingItem()
	{
		var pit = NewPit("fresh-stamp-on-add");

		// Construct an incoming item the way an upstream snapshot or a copy
		// constructor would: with an explicit, OLD Modified value.
		var oldModified = DateTimeOffset.UtcNow.AddDays(-3);
		var stale = new PitItem(ItemId, invalidate: false, timestamp: oldModified);
		stale["Payload"] = "first";

		var beforeAdd = DateTimeOffset.UtcNow;
		Assert.True(pit.Add(stale), "Pit.Add returned false on first insertion.");

		var stored = pit[ItemId];
		Assert.NotNull(stored);

		Assert.True(
			stored.Modified >= beforeAdd,
			$"BUG REPRODUCED: Pit.Add stored the inherited stale Modified " +
			$"({stored.Modified:O}) instead of stamping a fresh UtcNow " +
			$"(>= {beforeAdd:O}). The incoming item carried Modified={oldModified:O}; " +
			$"Pit.Add should have refreshed it.");

		Assert.NotEqual(oldModified, stored.Modified);
	}

	// =====================================================================
	// 2. THE "REPLICATED OLD TIMESTAMPS" SCENARIO
	//    Two distinct payloads, both inheriting the SAME old Modified
	//    (e.g. replayed from a snapshot, or produced by a copy ctor),
	//    must end up as two history fragments with two distinct fresh
	//    Modified values — not collapsed onto the shared old value.
	// =====================================================================
	[Fact]
	public void Pit_Add_TwoIncomingItemsWithSameOldModified_MustNotCloneItIntoHistory()
	{
		var pit = NewPit("no-clone-old-stamp");

		var sharedOld = DateTimeOffset.UtcNow.AddDays(-7);

		var a = new PitItem(ItemId, invalidate: false, timestamp: sharedOld);
		a["Payload"] = "A";
		var b = new PitItem(ItemId, invalidate: false, timestamp: sharedOld);
		b["Payload"] = "B";

		var beforeAdds = DateTimeOffset.UtcNow;
		Assert.True(pit.Add(a), "first Add was treated as a no-op.");
		Assert.True(pit.Add(b), "second Add was treated as a no-op.");

		var stamps = HistoryTimestamps(pit, ItemId);
		Assert.Equal(2, stamps.Count);

		// Bug surface 1: neither fragment may carry the old shared timestamp.
		Assert.DoesNotContain(stamps, t => t == sharedOld);

		// Bug surface 2: each fragment must be fresh (>= the moment Add was invoked).
		Assert.All(stamps, t => Assert.True(
			t >= beforeAdds,
			$"BUG REPRODUCED: history contains stale Modified {t:O} (older than " +
			$"the Add call instant {beforeAdds:O}); Pit.Add did not refresh it."));

		// Bug surface 3: the two fragments must not share a single Modified value.
		Assert.Equal(2, stamps.Select(t => t.UtcTicks).Distinct().Count());
	}

	// =====================================================================
	// 3. THE "COMPILING A HISTORY" SCENARIO
	//    Simulate ingesting a sequence of upstream snapshots (each parsed
	//    from JSON with its own pre-existing Modified) into a fresh Pit.
	//    Every accepted Add must result in a fresh, strictly increasing
	//    Modified — independent of the snapshot's own Modified value.
	// =====================================================================
	[Fact]
	public void Pit_Add_HistoryCompiledFromJsonSnapshots_GetsFreshModifiedPerFragment()
	{
		var pit = NewPit("compiled-history");

		// Three "upstream snapshots" with varied (and partly out-of-order) Modified
		// values, as a sync feed or replay log might deliver them.
		var t0 = DateTimeOffset.UtcNow.AddDays(-10);
		var t1 = DateTimeOffset.UtcNow.AddDays(-2);
		var t2 = DateTimeOffset.UtcNow.AddDays(-5);  // deliberately out of order

		var snapshots = new[]
		{
			JObject.Parse($"{{ \"Id\": \"{ItemId}\", \"Modified\": \"{t0:O}\", \"Deleted\": false, \"Payload\": 0 }}"),
			JObject.Parse($"{{ \"Id\": \"{ItemId}\", \"Modified\": \"{t1:O}\", \"Deleted\": false, \"Payload\": 1 }}"),
			JObject.Parse($"{{ \"Id\": \"{ItemId}\", \"Modified\": \"{t2:O}\", \"Deleted\": false, \"Payload\": 2 }}"),
		};

		var beforeAdds = DateTimeOffset.UtcNow;
		foreach (var snap in snapshots)
			Assert.True(pit.Add(new PitItem(snap)), "Pit.Add returned false during history compilation.");

		var stamps = HistoryTimestamps(pit, ItemId);
		Assert.Equal(3, stamps.Count);

		// None of the fragments may carry any of the old upstream timestamps.
		foreach (var old in new[] { t0, t1, t2 })
			Assert.DoesNotContain(stamps, t => t == old);

		// Every fragment's Modified must be fresh (>= start of the Add loop).
		Assert.All(stamps, t => Assert.True(
			t >= beforeAdds,
			$"BUG REPRODUCED: compiled history contains stale Modified {t:O} " +
			$"(older than the Add loop instant {beforeAdds:O})."));

		// And distinct payloads must end up with distinct timestamps.
		Assert.Equal(3, stamps.Select(t => t.UtcTicks).Distinct().Count());
	}

	// =====================================================================
	// 4. THE COPY-CONSTRUCTOR LEAK SCENARIO
	//    new PitItem(other) propagates Modified verbatim. If such a copy
	//    is mutated and re-added, Pit.Add must still stamp a fresh Now —
	//    not silently inherit `other.Modified`.
	// =====================================================================
	[Fact]
	public void Pit_Add_CopiedItem_DoesNotInheritSourceModified()
	{
		var pit = NewPit("copy-ctor-leak");

		var seed = new PitItem(ItemId, invalidate: false, timestamp: DateTimeOffset.UtcNow.AddHours(-6));
		seed["Payload"] = "seed";
		Assert.True(pit.Add(seed));
		var seedStored = pit[ItemId].Modified;

		// Make a copy, mutate it, re-add: Pit.Add must refresh Modified.
		var copy = new PitItem(seed);
		copy["Payload"] = "mutated";

		var beforeAdd = DateTimeOffset.UtcNow;
		Assert.True(pit.Add(copy), "Pit.Add of mutated copy returned false.");

		var stored = pit[ItemId];
		Assert.True(
			stored.Modified >= beforeAdd,
			$"BUG REPRODUCED: copied/mutated item kept the source's Modified " +
			$"({stored.Modified:O}); expected refresh to >= {beforeAdd:O}.");

		// And the new fragment must not equal the seed's stored Modified.
		Assert.NotEqual(seedStored, stored.Modified);
	}

	// =====================================================================
	// 5. CONCURRENT-WRITERS SCENARIO (regression guard)
	//    Many writers racing to Add distinct payloads for the same id must
	//    each get a unique Modified. Duplicates here would indicate either
	//    the staleness bug above or a separate clock-collision race.
	// =====================================================================
	[Fact]
	public void Pit_Add_Concurrent_HasNoDuplicateTimestamps()
	{
		var pit = NewPit("concurrent");
		const int writers = 8;
		const int perWriter = 25;
		var barrier = new Barrier(writers);
		var threads = new Thread[writers];
		for (int w = 0; w < writers; w++)
		{
			int writerId = w;
			threads[w] = new Thread(() =>
			{
				barrier.SignalAndWait();
				for (int i = 0; i < perWriter; i++)
				{
					var json = $"{{ \"Id\": \"{ItemId}\", \"Payload\": \"{writerId}-{i}\" }}";
					pit.Add(json);
				}
			});
		}
		foreach (var t in threads) t.Start();
		foreach (var t in threads) t.Join();

		var stamps = HistoryTimestamps(pit, ItemId);
		var distinct = new HashSet<long>(stamps.Select(s => s.UtcTicks));
		Assert.Equal(stamps.Count, distinct.Count);
	}
}
