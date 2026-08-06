using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

/// <summary>
/// CR003 §3 — explicit in-process concurrency tests on real configured cloud-root pits.
/// These tests create controlled races deliberately; they do not rely on incidental
/// test-runner parallelism.
/// </summary>
public sealed class InProcessConcurrencyTests : IDisposable
{
	private readonly List<RaiPath> cleanup = new();

	public void Dispose()
	{
		foreach (var root in cleanup)
			ConfiguredCloudPits.Cleanup(root);
	}

	private RaiPath NewPitRoot(string label)
	{
		var root = ConfiguredCloudPits.RequirePitRoot("in-process", $"{label}-{Guid.NewGuid():N}");
		root.mkdir();
		cleanup.Add(root);
		return root;
	}

	private static PitItem Fragment(string id, object payload)
	{
		var item = new PitItem(id);
		item.SetProperty(payload);
		return item;
	}

	[Fact]
	public void ConcurrentAdds_OnOneSharedPit_LoseNoAcceptedUpdate()
	{
		var root = NewPitRoot("no-loss");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		const int writers = 8;
		const int perWriter = 50;
		var accepted = new ConcurrentBag<string>();
		var barrier = new Barrier(writers);
		var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
		{
			barrier.SignalAndWait();
			for (var i = 0; i < perWriter; i++)
			{
				var id = $"W{w}-I{i}";
				if (pit.Add(Fragment(id, new { Writer = w, Seq = i })))
					accepted.Add(id);
			}
		})).ToArray();
		Task.WaitAll(tasks);

		Assert.Equal(writers * perWriter, accepted.Count);
		foreach (var id in accepted)
			Assert.NotNull(pit[id]);

		// Saving after concurrent additions produces a valid, complete pit file.
		pit.Save(force: true);
		var parsed = JArray.Parse(new TextFile(pit.JsonFile.FullName).ReadAllText());
		Assert.Equal(writers * perWriter, parsed.Count);
	}

	[Fact]
	public void DuplicateDetection_RemainsCorrect_UnderContention()
	{
		var root = NewPitRoot("duplicates");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		const int racers = 12;
		var acceptedCount = 0;
		var barrier = new Barrier(racers);
		var tasks = Enumerable.Range(0, racers).Select(_ => Task.Run(() =>
		{
			barrier.SignalAndWait();
			// Identical content for one id: only the first accepted addition may win;
			// every later identical add is a no-change duplicate.
			var item = new PitItem(new JObject
			{
				[nameof(PitItem.Id)] = "Contended",
				[nameof(PitItem.Modified)] = DateTimeOffset.UtcNow,
				[nameof(PitItem.Deleted)] = false,
				["Payload"] = "identical"
			});
			if (pit.Add(item))
				Interlocked.Increment(ref acceptedCount);
		})).ToArray();
		Task.WaitAll(tasks);

		Assert.Equal(1, acceptedCount);
		Assert.Equal(1, pit.HistoricItems["Contended"].Count);
	}

	[Fact]
	public void Save_ReleasesStateGateBeforeCloudIO_AdditionsContinueDuringPersistence()
	{
		var root = NewPitRoot("brief-gate");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		// Enough data to keep serialization plus cloud-file I/O observable.
		var payload = new string('x', 2048);
		for (var i = 0; i < 1500; i++)
			pit.Add(Fragment($"Seed-{i}", new { Payload = payload, Seq = i }));

		using var saveStarted = new ManualResetEventSlim(false);
		var saveTask = Task.Run(() =>
		{
			saveStarted.Set();
			pit.Save(force: true);
		});
		saveStarted.Wait();

		// Additions run while the save persists; they must never queue behind cloud I/O.
		var lateIds = new List<string>();
		for (var i = 0; i < 25; i++)
		{
			var id = $"Late-{i}";
			Assert.True(pit.Add(Fragment(id, new { Seq = i })));
			lateIds.Add(id);
		}
		saveTask.Wait();

		// An addition accepted after the snapshot boundary may be deferred, but its
		// fragment stays dirty until a snapshot containing it is persisted.
		pit.Save(); // unforced: writes precisely because the late fragments are still dirty
		var parsed = JArray.Parse(new TextFile(pit.JsonFile.FullName).ReadAllText());
		var persistedIds = parsed
			.SelectMany(history => (JArray)history)
			.Select(fragment => (string)fragment["Id"])
			.ToHashSet(StringComparer.Ordinal);
		foreach (var id in lateIds)
			Assert.Contains(id, persistedIds);
	}

	[Fact]
	public void LiveAdd_RefreshesModifiedAtInsertionBoundary_UniquenessIsNotPromised()
	{
		var root = NewPitRoot("timestamps");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		var stale = new PitItem("Staleness", invalidate: false, timestamp: DateTimeOffset.UtcNow.AddHours(-6));
		stale["Payload"] = "inherited-stale-timestamp";
		var before = DateTimeOffset.UtcNow;
		Assert.True(pit.Add(stale));
		var after = DateTimeOffset.UtcNow;
		var stored = pit["Staleness"].Modified;
		Assert.InRange(stored, before, after); // fresh UTC at the live insertion boundary

		// AddHistorical preserves the supplied historical timestamp verbatim.
		var historicalTime = DateTimeOffset.UtcNow.AddDays(-2);
		var historical = new PitItem("Historical", invalidate: false, timestamp: historicalTime);
		historical["Payload"] = "replayed";
		Assert.True(pit.AddHistorical(historical));
		Assert.Equal(historicalTime, pit["Historical"].Modified);
	}

	[Fact]
	public void HistoryOrdering_RemainsStableAndExplainable_AfterConcurrentAdds()
	{
		var root = NewPitRoot("ordering");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		const int writers = 6;
		var barrier = new Barrier(writers);
		var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
		{
			barrier.SignalAndWait();
			for (var i = 0; i < 20; i++)
				pit.Add(Fragment("Shared", new { Writer = w, Seq = i }));
		})).ToArray();
		Task.WaitAll(tasks);

		var history = pit.HistoricItems["Shared"].History;
		// Stable, explainable order: the deterministic comparator holds pairwise.
		for (var i = 0; i < history.Count - 1; i++)
			Assert.True(PitItems.CompareFragments(history[i], history[i + 1]) <= 0,
				$"History order violated the deterministic fragment comparator at index {i}.");
	}
}
