using System;
using System.Linq;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

/// <summary>Two actual processes, one configured cloud path, two stale replica views.
/// This checks C#/Python mutation and persistence interoperability; the separate
/// RemoteSync/RemoteCloudConcurrency suites establish cross-machine propagation.
/// </summary>
public sealed class CrossLanguageMutationTests
{
	private static RaiPath RepoRoot()
	{
		var directory = new RaiPath(AppContext.BaseDirectory);
		while (true)
		{
			if (new RaiFile(directory / "tests" / "cr077", "python_replica.py").Exists()) return directory;
			var parent = directory.Parent;
			if (parent.FullPath == directory.FullPath) break;
			directory = parent;
		}
		throw new InvalidOperationException("Run CR077 acceptance from a complete RAIkeep checkout.");
	}
	private static JsonDocument Python(RaiPath root, MutationTrackingMode mode, string operation)
	{
		var repo = RepoRoot();
		var script = new RaiFile(repo / "tests" / "cr077", "python_replica.py");
		var replica = new RaiSystem("python3", new[] { script.FullName, root.FullPath, mode.ToString(), operation })
		{
			EnvironmentOverrides = new System.Collections.Generic.Dictionary<string, string>
			{
				["PYTHONPATH"] = (repo / "JsonPit.Python").FullPath
			}
		};
		var result = replica.ExecResult(60_000);
		Assert.True(result.Succeeded, result.StandardError + result.StandardOutput);
		return JsonDocument.Parse(result.StandardOutput);
	}

	[Theory]
	[InlineData(MutationTrackingMode.TrackedChangesOnly)]
	[InlineData(MutationTrackingMode.TrackedChangesWithFallback)]
	public void StaleReplicaWritesKeepOtherFieldsAndPersistTombstones(MutationTrackingMode mode)
	{
		var root = ConfiguredCloudPits.RequirePitRoot("cr077-parity", Guid.NewGuid().ToString("N"));
		var previous = Pit.DefaultMutationTrackingMode;
		try
		{
			Pit.DefaultMutationTrackingMode = mode;
			using var pit = new Pit(root, readOnly: false, autoload: false, unflagged: true);
			var held = new PitItem("Ego") { ["Name"] = "Original", ["Status"] = "Pending", ["Namespace"] = "Old" };
			pit.Add(held); pit.Save(force: true);
			using var renamed = Python(root, mode, "rename");
			Assert.Equal("RemoteRename", renamed.RootElement.GetProperty("Name").GetString());
			// This process still holds its pre-replication view. Its next mutation
			// must not serialize the unrelated old Name/Namespace into history.
			Assert.Equal("Original", (string)held["Name"]);
			held.SetProperty(new { Status = "Doing" });
			var keys = pit.HistoricItems["Ego"].History[0].Properties().Select(p => p.Name).OrderBy(n => n).ToArray();
			Assert.Equal(new[] { "Deleted", "Id", "Modified", "Status" }, keys);
			Assert.True(pit.Load());
			Assert.Same(held, pit["Ego"]);
			Assert.Equal("RemoteRename", (string)held["Name"]);
			Assert.Equal("Doing", (string)held["Status"]);
			Assert.Null(held["Namespace"]);
			pit.Save(force: true);
			using var verified = Python(root, mode, "inspect");
			Assert.Equal("RemoteRename", verified.RootElement.GetProperty("Name").GetString());
			Assert.Equal("Doing", verified.RootElement.GetProperty("Status").GetString());
			Assert.False(verified.RootElement.GetProperty("NamespacePresent").GetBoolean());
		}
		finally { Pit.DefaultMutationTrackingMode = previous; ConfiguredCloudPits.Cleanup(root); }
	}
}
