using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

public sealed class MutationGuardrailContractTests
{
	[Theory]
	[InlineData(MutationTrackingMode.TrackedChangesOnly)]
	[InlineData(MutationTrackingMode.TrackedChangesWithFallback)]
	public void LiveReAddIsFlushOnlyAndDetachedCopyIsRejected(MutationTrackingMode mode)
	{
		var previous = Pit.DefaultMutationTrackingMode;
		var root = Os.TempDir / "RAIkeep" / "cr077" / Guid.NewGuid().ToString("N");
		try
		{
			Pit.DefaultMutationTrackingMode = mode;
			using var pit = new Pit(root, readOnly: false, autoload: false, unflagged: true);
			var item = new PitItem("Ego") { ["Name"] = "Original" };
			pit.Add(item);
			item.ExtendWith(new JObject { ["Id"] = "Ego", ["Status"] = "Doing" });
			var before = pit.HistoricItems[item.Id].Count;
			Assert.False(pit.Add(item));
			Assert.Equal(before, pit.HistoricItems[item.Id].Count);
			Assert.Same(item, pit[item.Id]);
			Assert.Equal(new[] { "Deleted", "Id", "Modified", "Status" }, pit.HistoricItems[item.Id].History[0].Properties().Select(p => p.Name).OrderBy(n => n));
			Assert.Throws<ProtectedAttributeException>(() => pit.Add(new PitItem((JObject)item.DeepClone())));
			Assert.Throws<ProtectedAttributeException>(() => item.ExtendWith(new JObject { ["Status"] = "Bad", ["Modified"] = "bad" }));
			Assert.Equal("Doing", (string)item["Status"]);
			Assert.Equal(before, pit.HistoricItems[item.Id].Count);
		}
		finally
		{
			Pit.DefaultMutationTrackingMode = previous;
			if (root.Exists()) root.rmdir(depth: 5, deleteFiles: true);
		}
	}
	[Fact]
	public void HistoricalReplayCannotBypassLiveOwnership()
	{
		var root = Os.TempDir / "RAIkeep" / "cr077" / Guid.NewGuid().ToString("N");
		try
		{
			using var first = new Pit(root / "First", readOnly: false, autoload: false, unflagged: true);
			using var second = new Pit(root / "Second", readOnly: false, autoload: false, unflagged: true);
			var item = new PitItem("Ego"); first.Add(item);
			Assert.Throws<InvalidOperationException>(() => second.Add(item));
			Assert.Throws<InvalidOperationException>(() => second.AddHistorical(item));
			Assert.False(second.Contains(item.Id));
		}
		finally { if (root.Exists()) root.rmdir(depth: 5, deleteFiles: true); }
	}

}
