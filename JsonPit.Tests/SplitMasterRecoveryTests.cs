using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

/// <summary>
/// CR003 / JsonPit-CONCEPT-Live-Split-Master-Recovery — deterministic split-master
/// recovery tests on real configured cloud-root pits. The competing claimant and the
/// provider's conflict artifact are represented by their exact on-disk footprints
/// (canonical <c>Master.flag</c> content and a longer <c>Master*.flag</c> conflict
/// copy), planted exactly as the provider and the other process would materialize them.
/// The live two-server reproduction runs in <see cref="RemoteCloudConcurrencyTests"/>.
/// </summary>
public sealed class SplitMasterRecoveryTests : IDisposable
{
	private readonly List<RaiPath> cleanup = new();
	private readonly TimeSpan originalDebounce = Pit.RecoveryDebounce;
	private readonly TimeSpan originalOrphanGrace = Pit.OrphanedConflictFlagGrace;

	public SplitMasterRecoveryTests()
	{
		Pit.RecoveryDebounce = TimeSpan.FromMilliseconds(200);
	}

	public void Dispose()
	{
		Pit.RecoveryDebounce = originalDebounce;
		Pit.OrphanedConflictFlagGrace = originalOrphanGrace;
		foreach (var root in cleanup)
			ConfiguredCloudPits.Cleanup(root);
	}

	private RaiPath NewPitRoot(string label, bool cloud = false)
	{
		var name = $"{label}-{Guid.NewGuid():N}";
		var root = cloud ? ConfiguredCloudPits.CreateTimedPitRoot("split-master", name) : (Os.TempDir / name).mkdir();
		cleanup.Add(root);
		return root;
	}

	private static void PlantConflictFlag(RaiPath pitDir, string name, string claimantIdentity, DateTimeOffset time)
	{
		var flag = new TextFile(pitDir, name, "flag")
		{
			Lines = [new TimestampedValue(claimantIdentity, time).ToString()],
			Changed = true
		};
		flag.Save();
	}

	private static void PlantProcessWindow(RaiPath pitDir, string exactIdentity, DateTimeOffset time)
	{
		var window = new TextFile(pitDir, exactIdentity, "flag")
		{
			Lines = [new TimestampedValue(exactIdentity.Replace('-', ':'), time).ToString()],
			Changed = true
		};
		window.Save();
	}

	private static bool WaitFor(Func<bool> condition, int timeoutMs = 15000)
	{
		var deadline = Environment.TickCount64 + timeoutMs;
		while (Environment.TickCount64 < deadline)
		{
			if (condition()) return true;
			Thread.Sleep(100);
		}
		return condition();
	}

	[Fact]
	[Trait("Category", "CloudEffect")]
	public void LiveLoser_WatcherDetectsConflict_PublishesWriteSet_RetiresOnlyItsLongerFlag()
	{
		var root = NewPitRoot("live-loser", cloud: true);
		var statuses = new List<RecoveryStatus>();
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.RecoveryStatusChanged += status => { lock (statuses) statuses.Add(status); };

		// Complete a canonical turn before a later conflict arrives.
		var itemA = new PitItem("TenureA"); itemA.SetProperty(new { Payload = "a" });
		var itemB = new PitItem("TenureB"); itemB.SetProperty(new { Payload = "b" });
		pit.Add(itemA);
		pit.Add(itemB);
		pit.Save(force: true);
		// Plus a currently dirty fragment accepted after the last snapshot.
		var dirty = new PitItem("DirtyC"); dirty.SetProperty(new { Payload = "c" });
		pit.Add(dirty);

		// The provider materializes the losing outcome: canonical Master.flag names the
		// other claimant; the longer conflict copy carries this process's exact claim.
		var foreignWinner = "OtherServer-cr003-424242";
		TicketFixture.WriteMaster(pit.MasterFlag(), originator: foreignWinner);
		PlantConflictFlag(root, "Master (1)", pit.ExactProcessIdentity, DateTimeOffset.UtcNow);

		// The native watcher queues one debounced recovery evaluation.
		Assert.True(WaitFor(() => !new RaiFile(root, "Master (1)", "flag").Exists()),
			"The live loser must retire its own longer conflict flag after its recovery files validate locally.");

		// The returned turn is already durable. Only the later dirty edit needs recovery.
		var changeFiles = root.EnumerateFiles("*.json")
			.Where(f => f.Name != pit.JsonFile.Name)
			.Where(f => ChangeFile.IdentityOf(f.Name) == pit.ExactProcessIdentity)
			.ToList();
		Assert.Equal("DirtyC", (string)Assert.Single(ChangeFile.ReadValidated(Assert.Single(changeFiles)))["Id"]);
		Assert.Contains("TenureA", new TextFile(pit.JsonFile.FullName).ReadFromDisk());
		Assert.Contains("TenureB", new TextFile(pit.JsonFile.FullName).ReadFromDisk());

		// Exact canonical Master.flag was never deleted or altered by the loser.
		Assert.Equal(foreignWinner, new MasterFlagFile(root, "Master").Originator);

		// Live status and durable audit stages exist without requiring a subscriber.
		Assert.NotNull(pit.LastRecoveryStatus);
		Assert.Equal(RecoveryStage.Completed, pit.LastRecoveryStatus.Stage);
		lock (statuses)
		{
			Assert.Contains(statuses, s => s.Stage == RecoveryStage.ConflictDetected);
			Assert.Contains(statuses, s => s.Stage == RecoveryStage.RoleDetermined && s.Role == RecoveryRole.Loser);
			Assert.Contains(statuses, s => s.Stage == RecoveryStage.ChangeFilesPublished);
		}
		var events = EventDirectory.Events(root);
		Assert.Contains(events, entry => entry.Key.EndsWith("_ConflictDetected.event", StringComparison.Ordinal)
			&& (string)entry.Value["Stage"] == nameof(RecoveryStage.ConflictDetected));
		lock (statuses) Assert.Contains(statuses, status => status.Stage == RecoveryStage.Completed);
	}

	private static void AssertReloadRecovery(Pit pit, RecoveryRole role, RecoveryStage stage)
	{
#pragma warning disable RAI013 // The pinned 4.6.7 analyzer predates CR095. Reload is the boundary under test.
		pit.Reload();
#pragma warning restore RAI013
		var status = pit.LastRecoveryStatus;
		Assert.NotNull(status);
		Assert.Equal(nameof(Pit.Reload), status.Operation);
		Assert.Equal(role, status.Role);
		Assert.Equal(stage, status.Stage);
	}

	[Fact]
	[Trait("Category", "CloudEffect")]
	public void MissedWatcherNotification_IsRecoveredAtTheNextOperationBoundary_WithoutPolling()
	{
		var root = NewPitRoot("missed-signal", cloud: true);
		Pit.RecoveryDebounce = TimeSpan.FromHours(1); // Defer the watcher until after the synchronous assertions.
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.Add(new PitItem("Tenure"));
		Assert.Equal(PitSaveOutcome.Wrote, pit.SaveWithOutcome(force: true).Outcome);
		Assert.Equal("Returned", pit.MasterFlag().ReadTicket().State);

		const string foreignWinner = "OtherServer-cr003-424242";
		TicketFixture.WriteMaster(pit.MasterFlag(), originator: foreignWinner);
		PlantConflictFlag(root, "Master (1)", pit.ExactProcessIdentity, DateTimeOffset.UtcNow);
		var conflict = new RaiFile(root, "Master (1)", "flag");
		pit.Add(new PitItem("TriggersBoundaryScan"));
		Assert.True(conflict.Exists());

		AssertReloadRecovery(pit, RecoveryRole.Loser, RecoveryStage.Completed);

		Assert.False(conflict.Exists());
		Assert.Equal(foreignWinner, pit.MasterFlag().Originator);
	}

	[Fact]
	[Trait("Category", "CloudEffect")]
	public void CurrentMaster_LeavesStillLiveLosersConflictEvidenceAlone()
	{
		var root = NewPitRoot("live-claimant", cloud: true);
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		Assert.True(TicketFixture.AttemptClaim(pit).Acquired);

		var liveClaimant = $"{Environment.MachineName}-ghost-424242";
		PlantConflictFlag(root, "Master (1)", liveClaimant, DateTimeOffset.UtcNow);
		PlantProcessWindow(root, liveClaimant, DateTimeOffset.UtcNow); // claimant window is ACTIVE

		pit.Save(); // boundary scan runs the master-side evaluation

		Assert.True(new RaiFile(root, "Master (1)", "flag").Exists(),
			"The current master must not retire a still-live loser's conflict evidence on its behalf.");
	}

	[Fact]
	[Trait("Category", "CloudEffect")]
	public void OrphanedConflictFlag_IsRetiredOnlyAfterExpiryPlusGrace_WithValidatedCriticalEvidence()
	{
		var root = NewPitRoot("orphan", cloud: true);
		Pit.RecoveryDebounce = TimeSpan.FromHours(1);
		Pit.OrphanedConflictFlagGrace = TimeSpan.FromMinutes(2);
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		Assert.True(TicketFixture.AttemptClaim(pit).Acquired);
		var held = pit.MasterFlag().ReadTicket();
		Assert.Equal("Held", held.State);
		Assert.Equal(pit.ExactProcessIdentity, held.Owner);

		var deadClaimant = $"{Environment.MachineName}-ghost-424242";
		var conflict = new RaiFile(root, "Master (1)", "flag");
		var expiryBoundary = DateTimeOffset.UtcNow - MasterFlagFile.TicketDuration;

		// The claimant has expired, but is still inside the grace period.
		PlantConflictFlag(root, "Master (1)", deadClaimant, expiryBoundary - TimeSpan.FromSeconds(1));
		AssertReloadRecovery(pit, RecoveryRole.Master, RecoveryStage.CleanupPending);
		Assert.True(conflict.Exists());

		// Scan again under the same held ticket once the evidence is older than expiry plus grace.
		PlantConflictFlag(root, "Master (1)", deadClaimant, expiryBoundary - Pit.OrphanedConflictFlagGrace - TimeSpan.FromSeconds(1));
		AssertReloadRecovery(pit, RecoveryRole.Master, RecoveryStage.Completed);

		Assert.False(conflict.Exists());
		Assert.Equal(held, pit.MasterFlag().ReadTicket());
		Assert.Contains(EventDirectory.Events(root).Values, e =>
			(string)e["Level"] == "Critical" && (string)e["Operation"] == nameof(Pit.Reload)
			&& ((string)e["Message"]).Contains("Master (1).flag") && ((string)e["Message"]).Contains(deadClaimant));
	}

	[Fact]
	public void ForeignTakeoverAfterReturnDoesNotRepublishCleanFragments()
	{
		var root = NewPitRoot("transfer");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.Add(new PitItem("TenureFragment"));
		pit.Save(force: true);
		Assert.Equal("Returned", pit.MasterFlag().ReadTicket().State);
		TicketFixture.WriteMaster(pit.MasterFlag(), originator: "OtherServer-cr003-424242");
		Assert.False(TicketFixture.AttemptClaim(pit).Acquired);
		Assert.Empty(root.EnumerateFiles("*.json"));
	}

	[Fact]
	[Trait("Category", "CloudEffect")]
	public void DisposedPit_StopsWatcher_NoRecoveryRunsForItAfterDisposal()
	{
		var root = NewPitRoot("disposed-watcher", cloud: true);
		var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.Dispose(); // empty pit: disposal publishes nothing

		PlantConflictFlag(root, "Master (1)", $"{Environment.MachineName}-ghost-424242", DateTimeOffset.UtcNow);
		Thread.Sleep(1000); // well beyond the shortened debounce

		Assert.True(new RaiFile(root, "Master (1)", "flag").Exists(),
			"No recovery may run for a disposed pit; the conflict signal must remain untouched.");
		Assert.Empty(EventDirectory.Events(root));
	}

	[Fact]
	public void GracefulDisposal_AfterReturnPublishesOnlyOutstandingDirtyFragments()
	{
		var root = NewPitRoot("graceful-disposal");
		var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		var persisted = new PitItem("PersistedInTenure"); persisted.SetProperty(new { Payload = 1 });
		pit.Add(persisted);
		Assert.Equal(PitSaveOutcome.Wrote, pit.SaveWithOutcome(force: true).Outcome);
		var dirty = new PitItem("StillDirty"); dirty.SetProperty(new { Payload = 2 });
		pit.Add(dirty); // never canonically saved before disposal
		var exactIdentity = pit.ExactProcessIdentity;
		var canonicalName = pit.JsonFile.Name;

		pit.Dispose();

		// Verify each edit at its durability boundary after explicit return.
		var changeFiles = root.EnumerateFiles("*.json")
			.Where(f => f.Name != canonicalName)
			.Where(f => ChangeFile.IdentityOf(f.Name) == exactIdentity)
			.ToList();
		Assert.Equal("StillDirty", (string)Assert.Single(ChangeFile.ReadValidated(Assert.Single(changeFiles)))["Id"]);
		Assert.Contains("PersistedInTenure", new TextFile(pit.JsonFile.FullName).ReadFromDisk());

		// Authority was released only afterwards: the PID-specific process window is gone.
		var window = new MasterFlagFile(root, ProcessFlagFile.CurrentFlagName("cr003"));
		Assert.True(window.IsExpired, "The process activity window must be released during graceful disposal.");
	}

	[Fact]
	public void Finalizer_PerformsNoRecoveryPublicationOrFilesystemIO_AndPathBecomesReopenable()
	{
		var root = NewPitRoot("finalizer");
		var abandoned = CreateAndAbandonPit(root);
		var filesBefore = root.EnumerateFiles("*").Select(f => f.NameWithExtension).OrderBy(n => n).ToList();

		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();

		Assert.False(abandoned.TryGetTarget(out _),
			"The native watcher and any queued debounce must not keep an abandoned Pit alive.");

		// The finalizer wrote no change file, flag, event, or canonical content.
		var filesAfter = root.EnumerateFiles("*").Select(f => f.NameWithExtension).OrderBy(n => n).ToList();
		Assert.Equal(filesBefore, filesAfter);
		Assert.Empty(EventDirectory.Events(root));

		// The abandoned instance no longer counts as the live owner: reopen succeeds.
		using var reopened = new Pit(root, readOnly: false, autoload: true, subscriber: "cr003");
		Assert.NotNull(reopened["Persisted"]);
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	private static WeakReference<Pit> CreateAndAbandonPit(RaiPath root)
	{
		var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.Add(new PitItem("Persisted"));
		pit.Save(force: true);
		var dirty = new PitItem("AbandonedDirty");
		dirty.SetProperty(new { Payload = "lost-by-design-without-dispose" });
		pit.Add(dirty); // never exported: crash/abandonment is outside the live recovery guarantee
		return new WeakReference<Pit>(pit);
	}

	[Fact]
	public void EquivalentPropertyOrder_ProducesSameChangeFileHash_ChangedValueDoesNot()
	{
		var timestamp = DateTimeOffset.UtcNow;
		var orderedOneWay = new PitItem(PitJson.ParseObject(
			$"{{ \"Id\": \"X\", \"Modified\": \"{timestamp:o}\", \"Deleted\": false, \"Alpha\": 1, \"Beta\": 2 }}"));
		var orderedOtherWay = new PitItem(PitJson.ParseObject(
			$"{{ \"Beta\": 2, \"Alpha\": 1, \"Deleted\": false, \"Modified\": \"{timestamp:o}\", \"Id\": \"X\" }}"));
		var changedValue = new PitItem(PitJson.ParseObject(
			$"{{ \"Id\": \"X\", \"Modified\": \"{timestamp:o}\", \"Deleted\": false, \"Alpha\": 1, \"Beta\": 3 }}"));

		var (_, shaA) = ChangeFile.CanonicalPayloadFor(orderedOneWay);
		var (_, shaB) = ChangeFile.CanonicalPayloadFor(orderedOtherWay);
		var (_, shaC) = ChangeFile.CanonicalPayloadFor(changedValue);
		Assert.Equal(shaA, shaB);
		Assert.NotEqual(shaA, shaC);
	}

	[Fact]
	public void DistinctEqualTimestampFragments_WithDifferentChecksums_CoexistWithoutOverwriting()
	{
		var root = NewPitRoot("equal-ts-files");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.Add(new PitItem("Seed"));
		pit.Save(force: true);

		var timestamp = DateTimeOffset.Parse("2026-10-02T12:00:00Z");
		var one = new PitItem("EqualTs", invalidate: false, timestamp: timestamp); one["Payload"] = "one";
		var two = new PitItem("EqualTs", invalidate: false, timestamp: timestamp); two["Payload"] = "two";

		var fileOne = pit.CreateChangeFile(one, "RemotePeer-app-4242");
		var fileTwo = pit.CreateChangeFile(two, "RemotePeer-app-4242");
		Assert.NotEqual(fileOne.FullName, fileTwo.FullName);
		Assert.True(fileOne.Exists());
		Assert.True(fileTwo.Exists());
		Assert.Equal("one", (string)Assert.Single(ChangeFile.ReadValidated(fileOne))["Payload"]);
		Assert.Equal("two", (string)Assert.Single(ChangeFile.ReadValidated(fileTwo))["Payload"]);
		Assert.Equal(fileOne.FullName, pit.CreateChangeFile(one, "RemotePeer-app-4242").FullName);
	}

	[Fact]
	public void DistinctPayloads_WithSameTimestampIdentityAndChecksum_FailWithoutOverwriting()
	{
		var root = NewPitRoot("checksum-collision");
		using var pit = new Pit(root, readOnly: false, autoload: false, unflagged: true);
		var timestamp = DateTimeOffset.Parse("2026-10-02T12:00:00Z");
		var seen = new Dictionary<string, PitItem>(StringComparer.Ordinal);
		// 65,537 distinct payloads guarantee a repeated 16-bit prefix. Usually this
		// deterministic search needs only a few hundred, with no filesystem writes.
		for (var value = 0; value <= 65536; value++)
		{
			var candidate = new PitItem("Collision", invalidate: false, timestamp: timestamp) { ["Payload"] = value };
			var (_, sha) = ChangeFile.CanonicalPayloadFor(candidate);
			if (seen.TryAdd(sha[..4], candidate)) continue;
			var first = seen[sha[..4]];
			var original = pit.CreateChangeFile(first, "RemotePeer-app-4242");
			var originalBytes = original.ReadAllBytes();
			var error = Assert.Throws<JsonPitPersistenceException>(() => pit.CreateChangeFile(candidate, "RemotePeer-app-4242"));
			Assert.Contains("collision", error.Message, StringComparison.OrdinalIgnoreCase);
			Assert.Equal(originalBytes, original.ReadAllBytes());
			Assert.Equal((int)first["Payload"], (int)Assert.Single(ChangeFile.ReadValidated(original))["Payload"]);
			Assert.Single(root.EnumerateFiles("*.json"));
			return;
		}
		Assert.Fail("The finite four-character checksum space must contain a collision.");
	}

	[Fact]
	public void HashInvalidOrIncompleteChangeFile_IsNotMerged_NotDeleted_AndReconsideredWhenComplete()
	{
		var root = NewPitRoot("bad-change-file");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.Add(new PitItem("Seed"));
		pit.Save(force: true);

		var fragment = new PitItem("Recoverable", invalidate: false, timestamp: DateTimeOffset.UtcNow.AddMinutes(-1));
		fragment["Payload"] = "value";
		var (canonicalPayload, sha) = ChangeFile.CanonicalPayloadFor(fragment);
		var fileName = ChangeFile.ComposeName(fragment.Modified, "RemotePeer-app-4242", sha);
		var partialFile = new RaiFile(root, fileName, "json");
		// A partially materialized file: correct hashed name, truncated bytes.
		new TextFile(partialFile.FullName).WriteAllText(canonicalPayload[..(canonicalPayload.Length / 2)]);

		pit.MergeChanges();
		Assert.Null(pit["Recoverable"]); // not merged
		Assert.True(partialFile.Exists(), "An incomplete change file must not be deleted or marked processed.");

		// Once the bytes materialize completely, a later ordinary pass accepts it.
		new TextFile(partialFile.FullName).WriteAllText(canonicalPayload);
		pit.MergeChanges();
		Assert.NotNull(pit["Recoverable"]);
	}
}
