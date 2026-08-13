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

	private RaiPath NewPitRoot(string label)
	{
		var root = ConfiguredCloudPits.RequirePitRoot("split-master", $"{label}-{Guid.NewGuid():N}");
		root.mkdir();
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
	public void LiveLoser_WatcherDetectsConflict_PublishesWriteSet_RetiresOnlyItsLongerFlag()
	{
		var root = NewPitRoot("live-loser");
		var statuses = new List<RecoveryStatus>();
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.RecoveryStatusChanged += status => { lock (statuses) statuses.Add(status); };

		// A master tenure with canonically persisted fragments — the recovery write set.
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
		pit.MasterFlag().Update(originator: foreignWinner);
		PlantConflictFlag(root, "Master (1)", pit.ExactProcessIdentity, DateTimeOffset.UtcNow);

		// The native watcher queues one debounced recovery evaluation.
		Assert.True(WaitFor(() => !new RaiFile(root, "Master (1)", "flag").Exists()),
			"The live loser must retire its own longer conflict flag after its recovery files validate locally.");

		// The union of the tenure write set and dirty fragments became ordinary,
		// hash-validated change files — not one bulk pit dump.
		var changeFiles = root.EnumerateFiles("*.json")
			.Where(f => f.Name != pit.JsonFile.Name)
			.Where(f => ChangeFile.IdentityOf(f.Name) == pit.ExactProcessIdentity)
			.ToList();
		Assert.True(changeFiles.Count >= 3, $"Expected the tenure write set plus dirty fragments as small change files; found {changeFiles.Count}.");
		foreach (var file in changeFiles)
			Assert.NotNull(ChangeFile.ReadValidated(new RaiFile(file.FullName)));

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
		Assert.Contains(events.Keys, k => k.Contains("_ConflictDetected_"));
		Assert.Contains(events.Keys, k => k.Contains("_Completed_"));
	}

	[Fact]
	public void MissedWatcherNotification_IsRecoveredAtTheNextOperationBoundary_WithoutPolling()
	{
		var root = NewPitRoot("missed-signal");
		Pit.RecoveryDebounce = TimeSpan.FromHours(1); // watcher signal effectively never arrives
		try
		{
			using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
			pit.Add(new PitItem("Tenure"));
			pit.Save(force: true);

			pit.MasterFlag().Update(originator: "OtherServer-cr003-424242");
			PlantConflictFlag(root, "Master (1)", pit.ExactProcessIdentity, DateTimeOffset.UtcNow);
			Thread.Sleep(300);
			Assert.True(new RaiFile(root, "Master (1)", "flag").Exists(), "Precondition: the signal was not consumed yet.");

			pit.Add(new PitItem("TriggersBoundaryScan"));
			pit.Save(); // Save's operation-boundary scan performs the recovery synchronously

			Assert.False(new RaiFile(root, "Master (1)", "flag").Exists(),
				"The operation-boundary scan must recover a missed watcher notification without any polling loop.");
			Assert.Equal(RecoveryStage.Completed, pit.LastRecoveryStatus.Stage);
		}
		finally
		{
			Pit.RecoveryDebounce = TimeSpan.FromMilliseconds(200);
		}
	}

	[Fact]
	public void CurrentMaster_LeavesStillLiveLosersConflictEvidenceAlone()
	{
		var root = NewPitRoot("live-claimant");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.Add(new PitItem("MasterData"));
		pit.Save(force: true); // this process is the exact canonical master

		var liveClaimant = $"{Environment.MachineName}-ghost-424242";
		PlantConflictFlag(root, "Master (1)", liveClaimant, DateTimeOffset.UtcNow);
		PlantProcessWindow(root, liveClaimant, DateTimeOffset.UtcNow); // claimant window is ACTIVE

		pit.Save(); // boundary scan runs the master-side evaluation

		Assert.True(new RaiFile(root, "Master (1)", "flag").Exists(),
			"The current master must not retire a still-live loser's conflict evidence on its behalf.");
	}

	[Fact]
	public void OrphanedConflictFlag_IsRetiredOnlyAfterExpiryPlusGrace_WithValidatedCriticalEvidence()
	{
		var root = NewPitRoot("orphan");
		Pit.OrphanedConflictFlagGrace = TimeSpan.FromSeconds(1);
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.Add(new PitItem("MasterData"));
		pit.Save(force: true); // this process is the exact canonical master

		var deadClaimant = $"{Environment.MachineName}-ghost-424242";

		// Not yet eligible: the claim is recent, so expiry plus grace has not elapsed.
		PlantConflictFlag(root, "Master (1)", deadClaimant, DateTimeOffset.UtcNow);
		pit.Save();
		Assert.True(new RaiFile(root, "Master (1)", "flag").Exists(),
			"An orphaned signal must survive until process-window expiry plus the safety grace.");

		// Eligible: claim and (absent) window lie beyond TicketDuration plus grace.
		PlantConflictFlag(root, "Master (1)", deadClaimant, DateTimeOffset.UtcNow - MasterFlagFile.TicketDuration - TimeSpan.FromMinutes(2));
		pit.Save();

		Assert.False(new RaiFile(root, "Master (1)", "flag").Exists(),
			"Only the exact current master may retire the orphaned longer flag after expiry plus grace.");
		// Exact canonical Master.flag remains untouched and still names this process.
		Assert.Equal(pit.ExactProcessIdentity, new MasterFlagFile(root, "Master").Originator);
		// The validated Critical evidence event exists and names the conflicting file.
		var events = EventDirectory.Events(root);
		var critical = events.Values.Where(e => (string)e["Level"] == "Critical").ToList();
		Assert.NotEmpty(critical);
		Assert.Contains(critical, e => ((string)e["Message"]).Contains("Master (1).flag"));
		Assert.Contains(critical, e => ((string)e["Message"]).Contains(deadClaimant));
	}

	[Fact]
	public void LiveAuthorityTransfer_ExportsTenureWriteSet_SameProcessReacquisitionDoesNot()
	{
		var root = NewPitRoot("transfer");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.Add(new PitItem("TenureFragment"));
		pit.Save(force: true);

		int ChangeFileCount() => root.EnumerateFiles("*.json").Count(f => f.Name != pit.JsonFile.Name);

		// Mere lease expiry followed by reacquisition by the same exact process is not a
		// transfer and must not trigger the durable handoff export.
		pit.MasterFlag().Update(DateTimeOffset.UtcNow - MasterFlagFile.TicketDuration - TimeSpan.FromSeconds(5),
			originator: pit.ExactProcessIdentity);
		Assert.True(pit.TryAcquireMaster());
		Assert.Equal(0, ChangeFileCount());

		// A live transfer to another exact process is a durability handoff even without
		// any conflict flag: the completed tenure's write set is exported.
		var newOwner = "OtherServer-cr003-424242";
		pit.MasterFlag().Update(originator: newOwner);
		PlantProcessWindow(root, newOwner, DateTimeOffset.UtcNow);
		Assert.False(pit.TryAcquireMaster());

		Assert.True(ChangeFileCount() >= 1, "The completed tenure's recovery write set must be exported as ordinary change files.");
		Assert.Contains(root.EnumerateFiles("*.json").Where(f => f.Name != pit.JsonFile.Name),
			f => ChangeFile.IdentityOf(f.Name) == pit.ExactProcessIdentity);
	}

	[Fact]
	public void DisposedPit_StopsWatcher_NoRecoveryRunsForItAfterDisposal()
	{
		var root = NewPitRoot("disposed-watcher");
		var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.Dispose(); // empty pit: disposal publishes nothing

		PlantConflictFlag(root, "Master (1)", $"{Environment.MachineName}-ghost-424242", DateTimeOffset.UtcNow);
		Thread.Sleep(1000); // well beyond the shortened debounce

		Assert.True(new RaiFile(root, "Master (1)", "flag").Exists(),
			"No recovery may run for a disposed pit; the conflict signal must remain untouched.");
		Assert.Empty(EventDirectory.Events(root));
	}

	[Fact]
	public void GracefulDisposal_PublishesWriteSetAndDirtyFragments_BeforeReleasingAuthority()
	{
		var root = NewPitRoot("graceful-disposal");
		var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		var persisted = new PitItem("PersistedInTenure"); persisted.SetProperty(new { Payload = 1 });
		pit.Add(persisted);
		pit.Save(force: true); // enters the tenure write set
		var dirty = new PitItem("StillDirty"); dirty.SetProperty(new { Payload = 2 });
		pit.Add(dirty); // never canonically saved before disposal
		var exactIdentity = pit.ExactProcessIdentity;
		var canonicalName = pit.JsonFile.Name;

		pit.Dispose();

		// Both fragments are durable as validated ordinary change files.
		var changeFiles = root.EnumerateFiles("*.json")
			.Where(f => f.Name != canonicalName)
			.Where(f => ChangeFile.IdentityOf(f.Name) == exactIdentity)
			.ToList();
		Assert.True(changeFiles.Count >= 2,
			$"Disposal must export the tenure write set plus dirty fragments; found {changeFiles.Count} change file(s).");
		foreach (var file in changeFiles)
			Assert.NotNull(ChangeFile.ReadValidated(new RaiFile(file.FullName)));

		// Authority was released only afterwards: the process window is tombstoned.
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
		var orderedOneWay = new PitItem(JObject.Parse(
			$"{{ \"Id\": \"X\", \"Modified\": \"{timestamp:o}\", \"Deleted\": false, \"Alpha\": 1, \"Beta\": 2 }}"));
		var orderedOtherWay = new PitItem(JObject.Parse(
			$"{{ \"Beta\": 2, \"Alpha\": 1, \"Deleted\": false, \"Modified\": \"{timestamp:o}\", \"Id\": \"X\" }}"));
		var changedValue = new PitItem(JObject.Parse(
			$"{{ \"Id\": \"X\", \"Modified\": \"{timestamp:o}\", \"Deleted\": false, \"Alpha\": 1, \"Beta\": 3 }}"));

		var (_, shaA) = ChangeFile.CanonicalPayloadFor(orderedOneWay);
		var (_, shaB) = ChangeFile.CanonicalPayloadFor(orderedOtherWay);
		var (_, shaC) = ChangeFile.CanonicalPayloadFor(changedValue);
		Assert.Equal(shaA, shaB);
		Assert.NotEqual(shaA, shaC);
	}

	[Fact]
	public void DistinctEqualTimestampFragments_ProduceDistinctChangeFiles_BothSurviveMerge()
	{
		var root = NewPitRoot("equal-ts-files");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.Add(new PitItem("Seed"));
		pit.Save(force: true);

		var timestamp = DateTimeOffset.UtcNow.AddMinutes(-1);
		var one = new PitItem("EqualTs", invalidate: false, timestamp: timestamp); one["Payload"] = "one";
		var two = new PitItem("EqualTs", invalidate: false, timestamp: timestamp); two["Payload"] = "two";

		var fileOne = pit.CreateChangeFile(one, "RemotePeer-app-4242");
		var fileTwo = pit.CreateChangeFile(two, "RemotePeer-app-4242");
		Assert.NotEqual(fileOne.FullName, fileTwo.FullName); // the hash distinguishes equal-time fragments

		pit.MergeChanges();
		Assert.Equal(2, pit.HistoricItems["EqualTs"].History.Count(f => f.Modified == timestamp));
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
		File.WriteAllText(partialFile.FullName, canonicalPayload[..(canonicalPayload.Length / 2)], new UTF8Encoding(false));

		pit.MergeChanges();
		Assert.Null(pit["Recoverable"]); // not merged
		Assert.True(partialFile.Exists(), "An incomplete change file must not be deleted or marked processed.");

		// Once the bytes materialize completely, a later ordinary pass accepts it.
		File.WriteAllText(partialFile.FullName, canonicalPayload, new UTF8Encoding(false));
		pit.MergeChanges();
		Assert.NotNull(pit["Recoverable"]);
	}
}
