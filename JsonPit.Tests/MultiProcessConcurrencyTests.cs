using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

/// <summary>
/// CR003 §5 — agreed v3.13.2 master ownership contract on real configured cloud-root
/// pits. Master ownership records the exact owning process (machine, subscriber, PID);
/// only that exact process renews directly. Other processes are represented here by
/// their exact on-disk footprint (Master.flag lease plus PID-specific activity window),
/// planted exactly as those processes would write them; true separate-process coverage
/// runs through the pits CLI suite and the remote scenario suite.
/// </summary>
public sealed class MultiProcessConcurrencyTests : IDisposable
{
	private readonly List<RaiPath> cleanup = new();
	private readonly TimeSpan originalTicketDuration = MasterFlagFile.TicketDuration;

	public void Dispose()
	{
		MasterFlagFile.TicketDuration = originalTicketDuration;
		foreach (var root in cleanup)
			ConfiguredCloudPits.Cleanup(root);
	}

	private RaiPath NewPitRoot(string label)
	{
		var root = ConfiguredCloudPits.RequirePitRoot("multi-process", $"{label}-{Guid.NewGuid():N}");
		root.mkdir();
		cleanup.Add(root);
		return root;
	}

	/// <summary>Plants the exact on-disk footprint of another process's activity window.</summary>
	private static void PlantProcessWindow(RaiPath pitDir, string exactIdentity, DateTimeOffset time)
	{
		var pid = exactIdentity.Split('-')[^1];
		var window = new TextFile(pitDir, exactIdentity, "flag")
		{
			Lines = [new TimestampedValue($"{Environment.MachineName}:planted:{pid}", time).ToString()],
			Changed = true
		};
		window.Save();
	}

	[Fact]
	public void ExactOwner_CannotReclaimDuringSelfBan()
	{
		var root = NewPitRoot("exact-renewal");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.Add(new PitItem("Seed"));
		pit.Save(force: true);
		Assert.Equal(pit.ExactProcessIdentity, pit.MasterFlag().Originator);
		var firstLease = pit.MasterFlag().Time;

		System.Threading.Thread.Sleep(20);
		Assert.False(TicketFixture.AttemptClaim(pit).Acquired);
		Assert.Equal(pit.ExactProcessIdentity, pit.MasterFlag().Originator);
		Assert.Equal(firstLease, pit.MasterFlag().Time);
	}

	[Fact]
	public void SameParticipantDifferentPid_MustNotInherit_WhileOwnersProcessWindowIsActive_AndFallsBackToChangeFiles()
	{
		var root = NewPitRoot("pid-refusal");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.Add(new PitItem("Seed"));
		pit.Save(force: true);

		// Another PID of the SAME stable participant holds the lease and an active window.
		var otherPid = $"{pit.ParticipantIdentity}-999999";
		TicketFixture.WriteMaster(pit.MasterFlag(), originator: otherPid);
		PlantProcessWindow(root, otherPid, DateTimeOffset.UtcNow);

		Assert.False(TicketFixture.AttemptClaim(pit).Acquired, "A different PID must not inherit while the recorded owner's window is active.");
		Assert.Equal(otherPid, pit.MasterFlag().Originator);

		var canonicalBefore = new TextFile(pit.JsonFile.FullName).ReadAllText();
		pit.Add(new PitItem("RefusedProcessChange"));
		pit.Save();

		// Change-file fallback: the canonical pit is untouched; the fragment is durable
		// as an ordinary collision-safe change file authored by this exact process.
		Assert.Equal(canonicalBefore, new TextFile(pit.JsonFile.FullName).ReadAllText());
		var changeFiles = root.EnumerateFiles("*.json")
			.Where(f => f.Name != pit.JsonFile.Name)
			.ToList();
		Assert.Contains(changeFiles, f => ChangeFile.IdentityOf(f.Name) == pit.ExactProcessIdentity);
	}

	[Fact]
	public void SameParticipantNewPid_StillDefers_AfterExplicitProcessWindowRelease()
	{
		var root = NewPitRoot("inherit-release");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.Add(new PitItem("Seed"));
		pit.Save(force: true);

		var otherPid = $"{pit.ParticipantIdentity}-999999";
		TicketFixture.WriteMaster(pit.MasterFlag(), originator: otherPid);
		// Explicitly released window: CR024 removes the owned PID-specific flag.
		// No activity file is planted for the former process.

		Assert.False(TicketFixture.AttemptClaim(pit).Acquired);
		Assert.Equal(otherPid, pit.MasterFlag().Originator);
	}

	[Fact]
	public void SameParticipantNewPid_StillDefers_AfterProcessWindowExpiry()
	{
		var root = NewPitRoot("inherit-expiry");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.Add(new PitItem("Seed"));
		pit.Save(force: true);

		var otherPid = $"{pit.ParticipantIdentity}-999999";
		TicketFixture.WriteMaster(pit.MasterFlag(), originator: otherPid);
		PlantProcessWindow(root, otherPid, DateTimeOffset.UtcNow - MasterFlagFile.TicketDuration - TimeSpan.FromSeconds(5));

		Assert.False(TicketFixture.AttemptClaim(pit).Acquired);
		Assert.Equal(otherPid, pit.MasterFlag().Originator);
	}

	[Fact]
	public void DifferentSubscribersOnOneMachine_RemainDistinctParticipants()
	{
		var root = NewPitRoot("distinct-subscribers");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.Add(new PitItem("Seed"));
		pit.Save(force: true);

		// A DIFFERENT subscriber on this machine holds a valid lease and active window.
		var foreign = $"{Environment.MachineName}-otherapp-999999";
		TicketFixture.WriteMaster(pit.MasterFlag(), originator: foreign);
		PlantProcessWindow(root, foreign, DateTimeOffset.UtcNow);

		Assert.False(TicketFixture.AttemptClaim(pit).Acquired, "Different subscribers are distinct stable participants; a valid foreign lease blocks the claim.");

		pit.Add(new PitItem("OtherSubscriberChange"));
		pit.Save(); // must take the ordinary non-master change-file path
		Assert.Contains(root.EnumerateFiles("*.json").Where(f => f.Name != pit.JsonFile.Name),
			f => ChangeFile.IdentityOf(f.Name) == pit.ExactProcessIdentity);
	}

	[Fact]
	public async System.Threading.Tasks.Task StaleWriter_DoesNotOverwriteNewerState_ChangesRemainMergeableAndDeterministic()
	{
		var root = NewPitRoot("stale-writer");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.Add(new PitItem("Seed"));
		pit.Save(force: true);
		var timing = PitTiming.Load(root);
		var localReady = DateTimeOffset.UtcNow + timing.PitSync + timing.Buffer;

		// This process becomes a stale writer: a foreign lease with an active window took over.
		var foreign = $"{Environment.MachineName}-takeover-999999";
		TicketFixture.WriteMaster(pit.MasterFlag(), originator: foreign);
		PlantProcessWindow(root, foreign, DateTimeOffset.UtcNow);

		var item = new PitItem("StaleWrite");
		item.SetProperty(new { Payload = "from-stale-writer" });
		pit.Add(item);
		pit.Save();
		var canonicalAfterStaleSave = new TextFile(pit.JsonFile.FullName).ReadAllText();
		Assert.DoesNotContain("StaleWrite", canonicalAfterStaleSave); // canonical not overwritten

		// Duplicate change files are safe: republication is filename-idempotent.
		var before = root.EnumerateFiles("*.json").Count(f => f.Name != pit.JsonFile.Name);
		pit.Save();
		var after = root.EnumerateFiles("*.json").Count(f => f.Name != pit.JsonFile.Name);
		Assert.Equal(before, after);

		// Once the foreign lease and window lapse, the merge protocol folds the change in
		// deterministically — the accepted write was never lost.
		TicketFixture.WriteMaster(pit.MasterFlag(), DateTimeOffset.UtcNow - MasterFlagFile.TicketDuration - TimeSpan.FromSeconds(5), originator: foreign);
		PlantProcessWindow(root, foreign, DateTimeOffset.UtcNow - MasterFlagFile.TicketDuration - TimeSpan.FromSeconds(5));
		// Returning the seed ticket bars this machine until its propagation window has elapsed.
		var remaining = localReady - DateTimeOffset.UtcNow;
		if (remaining > TimeSpan.Zero) await System.Threading.Tasks.Task.Delay(remaining + TimeSpan.FromMilliseconds(1), TestContext.Current.CancellationToken);
		pit.MergeChanges();
		Assert.Contains("StaleWrite", new TextFile(pit.JsonFile.FullName).ReadAllText());
	}

	[Fact]
	public void OutOfOrderAndDelayedChangeFiles_MergeSafely()
	{
		var root = NewPitRoot("out-of-order");
		using var pit = new Pit(root, readOnly: false, autoload: false, subscriber: "cr003");
		pit.Add(new PitItem("Seed"));
		pit.Save(force: true);

		// Change files from a remote participant arrive in reverse history order and late.
		var baseTime = DateTimeOffset.UtcNow.AddMinutes(-30);
		var newer = new PitItem("Delayed", invalidate: false, timestamp: baseTime.AddMinutes(5));
		newer["Step"] = "second";
		var older = new PitItem("Delayed", invalidate: false, timestamp: baseTime);
		older["Step"] = "first";
		pit.CreateChangeFile(newer, "RemotePeer-app-4242");
		pit.CreateChangeFile(older, "RemotePeer-app-4242");

		pit.MergeChanges();

		// Ordered by fragment history, not arrival time.
		var history = pit.HistoricItems["Delayed"].History;
		Assert.Equal("second", (string)history[0]["Step"]);
		Assert.Equal("first", (string)history[1]["Step"]);

		// Exact replay duplicates from repeated delivery are ignored.
		pit.CreateChangeFile(older, "RemotePeer-app-4242");
		pit.MergeChanges();
		Assert.Equal(2, pit.HistoricItems["Delayed"].Count);
	}
}
