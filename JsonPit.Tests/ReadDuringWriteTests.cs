using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

/// <summary>
/// CR003 §5 — agreed v3.13.2 read-during-write contract on local
/// pits. The canonical writer of another process is represented by direct in-place
/// writes to the canonical file — exactly the bytes the exact master would produce.
/// </summary>
public sealed class ReadDuringWriteTests : IDisposable
{
	private readonly List<RaiPath> cleanup = new();
	private readonly int originalRetries = Pit.MaxLoadRetries;

	public void Dispose()
	{
		Pit.MaxLoadRetries = originalRetries;
		foreach (var root in cleanup)
			ConfiguredCloudPits.Cleanup(root);
	}

	private RaiPath NewPitRoot(string label)
	{
		var root = RAIkeepTestEnvironment.LocalPath("read-during-write", $"{label}-{Guid.NewGuid():N}");
		root.mkdir();
		cleanup.Add(root);
		return root;
	}

	private static string CanonicalPayload(params (string Id, string Value)[] items)
	{
		var histories = new JArray();
		foreach (var (id, value) in items)
		{
			histories.Add(new JArray(new JObject
			{
				["Id"] = id,
				["Modified"] = DateTimeOffset.UtcNow,
				["Deleted"] = false,
				["Value"] = value
			}));
		}
		return histories.ToString(Newtonsoft.Json.Formatting.None);
	}

	[Fact]
	public async Task OverlappedCanonicalWriteAndLoad_OnlyCompleteSnapshotsBecomeVisible()
	{
		var root = NewPitRoot("overlap");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.Add(new PitItem("Alpha"));
		pit.Add(new PitItem("Beta"));
		pit.Save(force: true);
		var canonicalPath = pit.JsonFile.FullName;

		var oldKeys = new[] { "Alpha", "Beta" };
		var newKeys = new[] { "Alpha", "Beta", "Gamma", "Delta" };
		var newContent = CanonicalPayload(newKeys.Select(k => (k, "v2")).ToArray());

		using var stopWriter = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var writer = Task.Run(() =>
		{
			// Another process's exact master rewriting the canonical in place, repeatedly.
			while (!stopWriter.IsCancellationRequested)
			{
				new TextFile(canonicalPath).WriteAllText(newContent);
				Thread.Yield();
			}
		}, stopWriter.Token);

		try
		{
			for (var i = 0; i < 25; i++)
			{
				Assert.True(pit.Load(undercover: true));
				var visible = pit.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
				// Every published state is one complete snapshot — never empty, partial, or mixed.
				Assert.True(
					visible.SequenceEqual(oldKeys.OrderBy(k => k, StringComparer.Ordinal)) ||
					visible.SequenceEqual(newKeys.OrderBy(k => k, StringComparer.Ordinal)),
					$"Published state was neither complete snapshot: [{string.Join(",", visible)}]");
				Assert.NotEmpty(visible);
			}
		}
		finally
		{
			stopWriter.Cancel();
			await writer;
		}
	}

	[Fact]
	public async Task TransientEmptyContentDuringKnownRewrite_IsRetried_PriorStatePreserved()
	{
		var root = NewPitRoot("transient-empty");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.Add(new PitItem("Kept"));
		pit.Save(force: true);
		var canonicalPath = pit.JsonFile.FullName;

		// A rewrite truncated the file; the complete content arrives while Load retries.
		new TextFile(canonicalPath).WriteAllText(string.Empty);
		var completer = Task.Run(async () =>
		{
			await Task.Delay(400, TestContext.Current.CancellationToken);
			new TextFile(canonicalPath).WriteAllText(CanonicalPayload(("Kept", "v2")));
		}, TestContext.Current.CancellationToken);

		Assert.True(pit.Load(undercover: true));
		await completer;
		Assert.NotNull(pit["Kept"]); // never replaced by transiently empty state
		Assert.Equal("v2", (string)pit["Kept"]["Value"]);
	}

	[Fact]
	public async Task TransientAbsenceDuringKnownRewrite_IsRetried_NotReportedAsNoData()
	{
		var root = NewPitRoot("transient-missing");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.Add(new PitItem("Kept"));
		pit.Save(force: true);
		var canonicalPath = pit.JsonFile.FullName;

		// The known-to-exist canonical is briefly absent, then rematerializes.
		new RaiFile(canonicalPath).rm();
		var completer = Task.Run(async () =>
		{
			await Task.Delay(400, TestContext.Current.CancellationToken);
			new TextFile(canonicalPath).WriteAllText(CanonicalPayload(("Kept", "v3")));
		}, TestContext.Current.CancellationToken);

		Assert.True(pit.Load(undercover: true));
		await completer;
		Assert.Equal("v3", (string)pit["Kept"]["Value"]);
	}

	[Fact]
	public void BoundedRetryExhaustion_ThrowsDescriptivePersistenceException_AndPreservesInMemoryState()
	{
		var root = NewPitRoot("exhaustion");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.Add(new PitItem("Survivor"));
		pit.Save(force: true);
		var canonicalPath = pit.JsonFile.FullName;

		Pit.MaxLoadRetries = 1; // keep the bounded window short for the test
		try
		{
			// Permanently incomplete content: every bounded attempt fails to parse.
			new TextFile(canonicalPath).WriteAllText("[[{\"Id\":\"Broken");

			var ex = Assert.Throws<JsonPitPersistenceException>(() => pit.Load(undercover: true));
			Assert.Contains(canonicalPath, ex.Message);
			Assert.Contains("preserved", ex.Message);

			// The prior in-memory state was neither cleared nor partially replaced.
			Assert.NotNull(pit["Survivor"]);
		}
		finally
		{
			Pit.MaxLoadRetries = originalRetries;
		}
	}

	[Fact]
	public void GenuinelyAbsentPitOnInitialCreation_RemainsAValidNoDataCase()
	{
		var root = NewPitRoot("initial-absence");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		Assert.False(pit.Load(undercover: true)); // no exception — valid no-data
		Assert.Empty(pit.Keys);
	}
}
