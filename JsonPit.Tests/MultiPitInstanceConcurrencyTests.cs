using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

/// <summary>
/// CR003 §4 — one live public <see cref="Pit"/> per canonical pit path per process.
/// The rule applies to writable and read-only public instances alike; JsonPit rejects
/// the duplicate before it can load or mutate state.
/// </summary>
public sealed class MultiPitInstanceConcurrencyTests : IDisposable
{
	private readonly List<RaiPath> cleanup = new();

	public void Dispose()
	{
		foreach (var root in cleanup)
			ConfiguredCloudPits.Cleanup(root);
	}

	private RaiPath NewPitRoot(string label)
	{
		var root = ConfiguredCloudPits.RequirePitRoot("multi-instance", $"{label}-{Guid.NewGuid():N}");
		root.mkdir();
		cleanup.Add(root);
		return root;
	}

	[Fact]
	public void SecondWritableInstance_SameCanonicalPath_IsRejectedBeforeLoadingOrMutating()
	{
		var root = NewPitRoot("writable-duplicate");
		using var first = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		first.Add(new PitItem("Owned"));
		first.Save(force: true);

		var flagsBefore = root.EnumerateFiles("*.flag").Select(f => f.NameWithExtension).OrderBy(n => n).ToList();
		var canonicalBefore = File.ReadAllText(first.JsonFile.FullName);

		var ex = Assert.Throws<PitInstanceConflictException>(() =>
			new Pit(root, readOnly: false, autoload: true, subscriber: "cr003"));
		Assert.Equal(first.JsonFile.FullName, ex.CanonicalPath);
		Assert.Contains("singleton", ex.Message, StringComparison.OrdinalIgnoreCase);

		// Rejection happened before observable pit work: no new flags, no data mutation.
		var flagsAfter = root.EnumerateFiles("*.flag").Select(f => f.NameWithExtension).OrderBy(n => n).ToList();
		Assert.Equal(flagsBefore, flagsAfter);
		Assert.Equal(canonicalBefore, File.ReadAllText(first.JsonFile.FullName));
	}

	[Fact]
	public void SecondReadOnlyInstance_SameCanonicalPath_IsRejectedToo()
	{
		var root = NewPitRoot("readonly-duplicate");
		using var first = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		first.Add(new PitItem("Owned"));
		first.Save(force: true);

		Assert.Throws<PitInstanceConflictException>(() =>
			new Pit(root, readOnly: true, autoload: true, subscriber: "cr003"));
	}

	[Fact]
	public void PitFileConstructor_SameCanonicalPath_IsRejectedToo()
	{
		var root = NewPitRoot("pitfile-duplicate");
		using var first = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		first.Add(new PitItem("Owned"));
		first.Save(force: true);

		var segments = root.ToString().Split(Os.DIR, StringSplitOptions.RemoveEmptyEntries);
		Assert.Throws<PitInstanceConflictException>(() =>
			new Pit(new PitFile(root, segments[^1]), subscriber: "cr003", readOnly: true));
	}

	[Fact]
	public void Dispose_ReleasesPathOwnership_LegitimateReopenSucceeds()
	{
		var root = NewPitRoot("reopen");
		var first = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		first.Add(new PitItem("Persisted"));
		first.Save(force: true);
		first.Dispose();

		using var second = new Pit(root, readOnly: false, autoload: true, subscriber: "cr003");
		Assert.NotNull(second["Persisted"]);
	}

	[Fact]
	public void ConstructorFailure_DoesNotLeaveStalePathOwnership()
	{
		var root = NewPitRoot("ctor-failure");
		// Structurally incompatible canonical content makes construction fail after the
		// path reservation but before completion.
		var segments = root.ToString().Split(Os.DIR, StringSplitOptions.RemoveEmptyEntries);
		var canonical = new RaiFile(root, segments[^1], "pit");
		canonical.mkdir();
		File.WriteAllText(canonical.FullName, "{ \"not\": \"a pit\" }");

		Assert.ThrowsAny<FormatException>(() =>
			new Pit(root, readOnly: false, autoload: true, subscriber: "cr003"));

		// Repair the canonical and prove the path is reusable — no stale reservation.
		File.WriteAllText(canonical.FullName, "[]");
		using var recovered = new Pit(root, readOnly: false, autoload: true, subscriber: "cr003");
		Assert.True(recovered.Add(new PitItem("AfterRepair")));
	}

	[Fact]
	public void RacingConstructors_ExactlyOneWinsThePath()
	{
		var root = NewPitRoot("ctor-race");
		var winners = new List<Pit>();
		var rejections = 0;
		var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
		{
			try
			{
				var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
				lock (winners) winners.Add(pit);
			}
			catch (PitInstanceConflictException)
			{
				System.Threading.Interlocked.Increment(ref rejections);
			}
		})).ToArray();
		Task.WaitAll(tasks);

		Assert.Single(winners);
		Assert.Equal(5, rejections);
		winners[0].Dispose();
	}

	[Fact]
	public void InternalPersistenceComparisonAndMerge_UsePrivateSnapshotReaders_NotSecondPublicInstances()
	{
		var root = NewPitRoot("internal-readers");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.Add(new PitItem("Mine"));
		pit.Save(force: true);

		// Force the non-master change-file path: a valid foreign lease blocks this process.
		pit.MasterFlag().Update(originator: $"{Environment.MachineName}-foreign-999999");
		var window = new TextFile(root, $"{Environment.MachineName}-foreign-999999", "flag");
		window.Lines = new List<string> { new TimestampedValue($"{Environment.MachineName}:foreign:999999", DateTimeOffset.UtcNow).ToString() };
		window.Changed = true;
		window.Save();

		pit.Add(new PitItem("NonMasterChange"));
		// Save's comparison path and MergeChanges' parsing path must not construct a
		// second public Pit for the owned canonical path — no conflict may be thrown.
		pit.Save();
		pit.MergeChanges();
		Assert.NotNull(pit["NonMasterChange"]);
	}
}
