using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Newtonsoft.Json.Linq;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

public sealed class LiveReferenceTests : IDisposable
{
	private readonly List<RaiPath> roots = new();
	private Pit NewPit()
	{
		var root = Os.TempDir / "RAIkeep" / "live-reference-tests" / Guid.NewGuid().ToString("N");
		roots.Add(root);
		return new Pit(root, readOnly: false, autoload: false, unflagged: true);
	}
	public void Dispose()
	{
		foreach (var root in roots)
			if (root.Exists()) root.rmdir(depth: 5, deleteFiles: true);
	}
	private static PitItem Item() => new("Ego")
	{
		["Who"] = new JObject { ["Owner"] = "Unknown", ["Observer"] = "Adele" },
		["Role"] = "Person"
	};

	[Fact]
	public void OriginalAliases_AppendSparseFragments_WithoutChangingEarlierHistory()
	{
		using var pit = NewPit();
		var item = Item();
		var who = item["Who"];
		pit.Add(item);
		var initialTime = item.Modified;
		item["Instagram"] = "@Dr2RAI";
		who["Owner"] = "Rainer";

		Assert.Same(item, pit["Ego"]);
		Assert.Same(who, pit["Ego"]["Who"]);
		var history = pit.HistoricItems["Ego"].History;
		Assert.Equal(3, history.Count);
		Assert.Equal(new[] { "Deleted", "Id", "Modified", "Who" }, history[0].Properties().Select(p => p.Name).OrderBy(n => n));
		Assert.Single((JObject)history[0]["Who"]);
		Assert.Equal("Rainer", (string)history[0]["Who"]["Owner"]);
		Assert.Equal("Unknown", (string)pit.GetAt("Ego", initialTime)["Who"]["Owner"]);
		Assert.Null(pit.GetAt("Ego", initialTime)["Instagram"]);
	}

	[Fact]
	public void SilentScalarEdits_AreCoalescedAndTimestampedWhenSaveDetectsThem()
	{
		using var pit = NewPit();
		var item = Item();
		pit.Add(item);
		pit.Save();
		var originalTime = item.Modified;
		var owner = (JValue)item["Who"]["Owner"];
		owner.Value = "Intermediate";
		owner.Value = "Rainer";

		Assert.Equal("Rainer", (string)pit["Ego"]["Who"]["Owner"]);
		Assert.Single(pit.HistoricItems["Ego"].History);
		Assert.Equal(originalTime, item.Modified);
		Assert.Equal("Unknown", (string)pit.GetAt("Ego", DateTimeOffset.UtcNow)["Who"]["Owner"]);
		var detectionStarted = DateTimeOffset.UtcNow;
		pit.Save();

		Assert.InRange(item.Modified, detectionStarted, DateTimeOffset.UtcNow);
		Assert.Equal(2, pit.HistoricItems["Ego"].Count);
		Assert.Single((JObject)pit.HistoricItems["Ego"].History[0]["Who"]);
		Assert.Equal("Unknown", (string)pit.GetAt("Ego", originalTime)["Who"]["Owner"]);
		pit.Save();
		Assert.Equal(2, pit.HistoricItems["Ego"].Count);
	}

	[Fact]
	public void SparseAdd_PreservesLiveIdentityAndPreviouslySilentEdits()
	{
		using var pit = NewPit();
		var item = Item();
		pit.Add(item);
		var who = item["Who"];
		((JValue)who["Owner"]).Value = "Rainer";
		pit.Add(new PitItem("Ego") { ["Role"] = "Musician" });
		Assert.Same(item, pit["Ego"]);
		Assert.Same(who, item["Who"]);
		Assert.Equal("Rainer", (string)who["Owner"]);
		Assert.Equal("Musician", (string)item["Role"]);
		Assert.Equal(3, pit.HistoricItems["Ego"].Count);
	}

	[Fact]
	public void Load_PreservesSilentLocalEditsAndHeldReferences()
	{
		using var pit = NewPit();
		var item = Item();
		pit.Add(item);
		pit.Save();
		var who = item["Who"];
		((JValue)who["Owner"]).Value = "Rainer";
		Assert.True(pit.Load());
		Assert.Same(item, pit["Ego"]);
		Assert.Same(who, item["Who"]);
		Assert.Equal("Rainer", (string)who["Owner"]);
		Assert.Equal(2, pit.HistoricItems["Ego"].Count);
		Assert.True(pit.Invalid());
	}

	[Fact]
	public void HistoricalMerge_FoldsAtAcceptance_AndPreservesHeldReferences()
	{
		using var pit = NewPit();
		var time = DateTimeOffset.UtcNow.AddMinutes(-3);
		pit.AddHistorical(new PitItem("Ego", false, time)
		{
			["Who"] = new JObject { ["Owner"] = "Unknown", ["Observer"] = "Adele" }
		});
		var item = pit["Ego"];
		var who = item["Who"];
		var history = PitItems.Create("Ego")
			.Push(new PitItem("Ego", false, time.AddMinutes(2)) { ["Who"] = new JObject { ["Owner"] = "Latest" } })
			.Push(new PitItem("Ego", false, time.AddMinutes(1)) { ["Who"] = new JObject { ["Owner"] = "Earlier" } });
		pit.MergeIntoHistory(history);
		Assert.Same(item, pit["Ego"]);
		Assert.Same(who, item["Who"]);
		Assert.Equal("Latest", (string)who["Owner"]);
		Assert.Equal("Adele", (string)who["Observer"]);
		Assert.Equal("Earlier", (string)pit.GetAt("Ego", time.AddMinutes(1))["Who"]["Owner"]);
	}

	[Fact]
	public void Helpers_CommitOnce_AndTombstonesPruneTheLiveObject()
	{
		using var pit = NewPit();
		var item = Item();
		pit.Add(item);
		item.SetProperty(new { Instagram = "@Dr2RAI", Email = "example@example.org" });
		Assert.Equal(2, pit.HistoricItems["Ego"].Count);
		item.DeleteProperty("Instagram");
		Assert.Null(item["Instagram"]);
		item.DeletePropertyPath("Who.Owner");
		Assert.Null(item["Who"]["Owner"]);
		Assert.Equal("Adele", (string)item["Who"]["Observer"]);
		Assert.Equal(4, pit.HistoricItems["Ego"].Count);
		Assert.True(item.Delete());
		Assert.Null(pit["Ego"]);
	}

	[Fact]
	public void NativeMutations_WorkWithAdditionalObservers_AndPreserveSparseHistory()
	{
		using var pit = NewPit();
		var item = Item();
		item["Tags"] = new JArray("first");
		pit.Add(item);
		((INotifyCollectionChanged)item).CollectionChanged += (_, _) => { };
		item.Add("Extra", 1);
		item.Remove("Extra");
		item["Role"] = null;
		Assert.Null(item["Role"]);
		var who = (JObject)item["Who"];
		who.Remove("Owner");
		((JArray)item["Tags"]).Add("second");
		Assert.Equal(6, pit.HistoricItems["Ego"].Count);
		Assert.Equal(2, ((JArray)pit["Ego"]["Tags"]).Count);
		Assert.Null(pit["Ego"]["Who"]["Owner"]);
	}

	[Fact]
	public void ProtectedNativeAndSilentMutations_RollBackWithoutHistory()
	{
		using var pit = NewPit();
		var item = Item();
		pit.Add(item);
		Assert.Throws<ProtectedAttributeException>(() => item["Id"] = "Other");
		Assert.Throws<ProtectedAttributeException>(() => item.Remove("Id"));
		Assert.Equal("Ego", item.Id);
		((JValue)item["Id"]).Value = "Other";
		Assert.Throws<ProtectedAttributeException>(() => pit.Save());
		Assert.Equal("Ego", item.Id);
		Assert.Single(pit.HistoricItems["Ego"].History);
	}

	[Fact]
	public void HistoricalValuesAndSnapshots_AreDetachedFromLiveAndStoredObjects()
	{
		using var pit = NewPit();
		var item = Item();
		pit.Add(item);
		var at = item.Modified;
		pit.GetAt("Ego", at)["Who"]["Owner"] = "Past edited";
		pit.HistoricItems["Ego"].History[0]["Who"]["Owner"] = "History edited";
		pit.ValuesOverTime("Ego", "Who").First().Value["Owner"] = "Value edited";
		Assert.Equal("Unknown", (string)item["Who"]["Owner"]);
		Assert.Equal("Unknown", (string)pit.GetAt("Ego", at)["Who"]["Owner"]);
	}

	[Fact]
	public void Dispose_CapturesSilentEdits_AndReopenBuildsStableLiveObjects()
	{
		var pit = NewPit();
		var item = Item();
		pit.Add(item);
		((JValue)item["Who"]["Owner"]).Value = "Rainer";
		pit.Dispose();
		Assert.Throws<ObjectDisposedException>(() => item["Role"] = "Late edit");
		using var reopened = new Pit(pit.PitDir, readOnly: true, unflagged: true);
		var loaded = reopened["Ego"];
		Assert.Same(loaded, reopened["Ego"]);
		Assert.Equal("Rainer", (string)loaded["Who"]["Owner"]);
	}

	[Fact]
	public void TrackingMode_IsCapturedPerInstance_AndNeverPersisted()
	{
		var previous = Pit.DefaultMutationTrackingMode;
		try
		{
			Pit.DefaultMutationTrackingMode = MutationTrackingMode.TrackedChangesWithFallback;
			using var cli = NewPit();
			Pit.DefaultMutationTrackingMode = MutationTrackingMode.TrackedChangesOnly;
			using var server = NewPit();
			Assert.Equal(MutationTrackingMode.TrackedChangesWithFallback, cli.TrackingMode);
			Assert.Equal(MutationTrackingMode.TrackedChangesOnly, server.TrackingMode);
			cli.Add(Item());
			cli.Save();
			var bytes = new TextFile(cli.JsonFile.FullName).ReadAllText();
			Assert.DoesNotContain("TrackingMode", bytes);
			cli.Dispose();
			using var serverOpeningSameData = new Pit(cli.PitDir, readOnly: true, unflagged: true);
			Assert.Equal(MutationTrackingMode.TrackedChangesOnly, serverOpeningSameData.TrackingMode);
			Assert.NotNull(serverOpeningSameData["Ego"]);
		}
		finally { Pit.DefaultMutationTrackingMode = previous; }
	}

	[Theory]
	[InlineData(MutationTrackingMode.TrackedChangesOnly)]
	[InlineData(MutationTrackingMode.TrackedChangesWithFallback)]
	public void BothModes_KeepOriginalReferences_AndTrackIndexerEditsImmediately(MutationTrackingMode mode)
	{
		var previous = Pit.DefaultMutationTrackingMode;
		try
		{
			Pit.DefaultMutationTrackingMode = mode;
			using var pit = NewPit();
			var item = Item();
			var who = item["Who"];
			pit.Add(item);
			who["Owner"] = "Rainer";
			item["Instagram"] = "@Dr2RAI";
			Assert.Same(item, pit["Ego"]);
			Assert.Same(who, pit["Ego"]["Who"]);
			Assert.Equal(3, pit.HistoricItems["Ego"].Count);
			Assert.Equal("Rainer", (string)pit.GetAt("Ego", item.Modified)["Who"]["Owner"]);
		}
		finally { Pit.DefaultMutationTrackingMode = previous; }
	}

	[Fact]
	public void TrackedChangesOnly_DoesNotReconcileUnsupportedScalarEditsAtSave()
	{
		var previous = Pit.DefaultMutationTrackingMode;
		try
		{
			Pit.DefaultMutationTrackingMode = MutationTrackingMode.TrackedChangesOnly;
			using var pit = NewPit();
			var item = Item();
			pit.Add(item);
			pit.Save();
			((JValue)item["Who"]["Owner"]).Value = "Unreported";
			pit.Save();
			Assert.Single(pit.HistoricItems["Ego"].History);
			Assert.Equal("Unknown", (string)pit.GetAt("Ego", DateTimeOffset.UtcNow)["Who"]["Owner"]);
			Assert.DoesNotContain("Unreported", new TextFile(pit.JsonFile.FullName).ReadAllText());
		}
		finally { Pit.DefaultMutationTrackingMode = previous; }
	}

	[Fact]
	public void NestedTombstone_WithAdditionalObserver_PrunesImmediately()
	{
		using var pit = NewPit();
		var item = Item();
		pit.Add(item);
		var who = (JObject)item["Who"];
		((INotifyCollectionChanged)who).CollectionChanged += (_, _) => { };
		who["Owner"] = null;
		Assert.False(who.ContainsKey("Owner"));
		Assert.Equal(2, pit.HistoricItems["Ego"].Count);
	}

	[Fact]
	public void NewNestedTombstone_WithAdditionalObserver_DoesNotReenterNotifications()
	{
		using var pit = NewPit();
		var item = Item();
		pit.Add(item);
		var who = (JObject)item["Who"];
		((INotifyCollectionChanged)who).CollectionChanged += (_, _) => { };
		who["Absent"] = null;
		Assert.False(who.ContainsKey("Absent"));
		Assert.Same(who, pit["Ego"]["Who"]);
		Assert.Equal(2, pit.HistoricItems["Ego"].Count);
		Assert.Null(pit.HistoricItems["Ego"].History[0]["Who"]["Absent"]?.Value<string>());
	}

	[Fact]
	public void DeletedLifetime_IsNotReused_AndDetachedArraysCannotWriteBack()
	{
		using var pit = NewPit();
		var original = Item();
		var tags = new JArray("old");
		original["Tags"] = tags;
		pit.Add(original);
		original.SetProperty(new { Tags = new[] { "new" } });
		tags.Add("detached");
		Assert.Single((JArray)pit["Ego"]["Tags"]);
		Assert.Equal(2, pit.HistoricItems["Ego"].Count);
		Assert.True(pit.Delete("Ego"));
		var replacement = Item();
		pit.Add(replacement);
		Assert.Same(replacement, pit["Ego"]);
		Assert.Throws<InvalidOperationException>(() => original["Role"] = "Obsolete");
	}

	[Fact]
	public void Merge_DetectsSilentEditBeforeApplyingHistoricalChanges()
	{
		using var pit = NewPit();
		var item = Item();
		pit.Add(item);
		((JValue)item["Who"]["Owner"]).Value = "Rainer";
		var remote = new PitItem("Ego", false, DateTimeOffset.UtcNow)
		{
			["Role"] = "Remote update"
		};
		pit.MergeIntoHistory(PitItems.Create("Ego").Push(remote));
		Assert.Equal("Rainer", (string)item["Who"]["Owner"]);
		Assert.Equal("Remote update", (string)item["Role"]);
		Assert.Equal(3, pit.HistoricItems["Ego"].Count);
	}

	[Fact]
	public void NoOpMutationsAndCaseChanges_KeepTheSameLiveIdentity()
	{
		using var pit = NewPit();
		var item = Item();
		pit.Add(item);
		var modified = item.Modified;
		item["Role"] = "Person";
		item.SetProperty(new { Role = "Person" });
		Assert.False(pit.Add(item));
		Assert.Single(pit.HistoricItems["Ego"].History);
		Assert.Equal(modified, item.Modified);
		pit.IgnoreCase();
		Assert.Same(item, pit["EGO"]);
		pit.ConsiderCase();
		Assert.Same(item, pit["Ego"]);
		Assert.Null(pit["EGO"]);
	}
}
