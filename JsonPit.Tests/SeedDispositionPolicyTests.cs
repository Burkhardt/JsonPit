using System;
using JsonPit;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

public sealed class SeedDispositionPolicyTests
{
	[Theory]
	[InlineData(MutationTrackingMode.TrackedChangesOnly)]
	[InlineData(MutationTrackingMode.TrackedChangesWithFallback)]
	public void LatestFragmentEqualityStaysDistinctFromProjectedStateEquality(MutationTrackingMode mode)
	{
		var previous = Pit.DefaultMutationTrackingMode;
		var root = Os.TempDir / "RAIkeep" / "cr081-policy" / Guid.NewGuid().ToString("N");
		try
		{
			Pit.DefaultMutationTrackingMode = mode;
			using var pit = new Pit(root, readOnly: false, autoload: false, unflagged: true);
			var original = new PitItem("A") { ["Name"] = "same", ["Other"] = "retained" };
			Assert.True(pit.Add(original));
			var timestamp = original.Modified;
			Assert.False(pit.Add(new PitItem("A") { ["Name"] = "same", ["Other"] = "retained" }));
			Assert.Equal(timestamp, original.Modified);
			Assert.Single(pit.HistoricItems["A"].History);
			Assert.True(pit.Add(new PitItem("A") { ["Name"] = "same" }));
			Assert.Same(original, pit["A"]);
			Assert.Equal(2, pit.HistoricItems["A"].Count);
			Assert.True(pit.Add(new PitItem("A") { ["Other"] = null }));
			Assert.False(pit.Add(new PitItem("A") { ["Other"] = null }));
			Assert.Null(pit["A"]["Other"]);
			Assert.Equal(3, pit.HistoricItems["A"].Count);
			pit.Save();
		}
		finally
		{
			Pit.DefaultMutationTrackingMode = previous;
			if (root.Exists()) root.rmdir(depth: 8, deleteFiles: true);
		}
	}
}
