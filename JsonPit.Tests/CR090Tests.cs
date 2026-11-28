using System;
using System.Linq;
using System.Threading.Tasks;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

public sealed class CR090Tests : IDisposable
{
	private readonly RaiPath root = Os.TempDir / Guid.NewGuid().ToString("N");
	private RaiPath Directory => (root / "Tenant" / "Person").mkdir();
	private void Configure(double pit, double flag) =>
		new TextFile((root / "JsonPit").mkdir(), "timing", "json5").WriteAllText(
			FormattableString.Invariant($"{{TicketDurationSeconds:120,SyncPitDurationSeconds:{pit},SyncFlagDurationSeconds:{flag},BufferSeconds:0}}"));
	private RaiPath cloudDirectory;
	private RaiPath CloudDirectory() =>
		cloudDirectory = ConfiguredCloudPits.CreateTimedPitRoot("cr095-" + Guid.NewGuid().ToString("N"));
	public void Dispose()
	{
		ConfiguredCloudPits.Cleanup(cloudDirectory);
		if (root.Exists()) root.rmdir(depth: 6, deleteFiles: true);
	}

	[Fact]
	public void CalibratedJson5WithLeadingCommentsSetsTheActualWriteWindow()
	{
		new TextFile((root / "JsonPit").mkdir(), "timing", "json5").WriteAllText(
			"// Fleet measurements\n// Confirmed by operators\n{TicketDurationSeconds:150, SyncPitDurationSeconds:40, SyncFlagDurationSeconds:30, BufferSeconds:10,} // end");
		var directory = Directory;
		Assert.Equal(new PitTiming(TimeSpan.FromSeconds(150), TimeSpan.FromSeconds(40),
			TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10)), PitTiming.Load(directory));
		using var pit = new Pit(directory, readOnly: false);
		var start = DateTimeOffset.UtcNow;
		var claim = TicketFixture.AttemptClaim(pit);
		Assert.True(claim.Acquired);
		Assert.InRange(claim.EarliestWriteUtc, start, DateTimeOffset.UtcNow);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void OpenLoadAndDisposeLeaveNoFlags(bool readOnly)
	{
		var dir = Directory;
		new TextFile(dir, "Person", "pit", "[]");
		using (var pit = new Pit(dir, readOnly: readOnly)) pit.Load();
		Assert.Empty(dir.EnumerateFiles("*.flag"));
		Assert.False((dir / "Events").Exists());
	}

	[Theory]
	[InlineData("Mzansi", 20)]
	[InlineData("Mdlaka", 90)]
	[InlineData("Nkosikazi", 70)]
	[InlineData("Mzansi", 70)]
	[InlineData("Mzansi", -90)]
	[Trait("Category", "CloudEffect")]
	public void ReturnedPropagationWindow(string previousMachine, int age)
	{
		var dir = CloudDirectory();
		var timing = PitTiming.Load(dir);
		using var pit = new Pit(dir, readOnly: false);
		var now = DateTimeOffset.UtcNow;
		TicketFixture.WritePeerTicket(pit.MasterFlag(), new("Returned", "remote-app-1", now.AddMinutes(-3), now.AddSeconds(-age), previousMachine + "-fixture"));
		var turn = TicketFixture.AttemptClaim(pit);
		Assert.True(turn.Acquired);
		var due = TimeSpan.FromSeconds(Math.Max(timing.FlagSync.TotalSeconds,
			timing.PitSync.TotalSeconds - Math.Max(0, age))) + timing.Buffer;
		Assert.InRange(turn.EarliestWriteUtc, now + due - TimeSpan.FromMilliseconds(10), DateTimeOffset.UtcNow + due);
		Assert.False(TicketFixture.AttemptClaim(pit).Acquired);
	}

	[Fact]
	public void LegacyLeaseAndInvalidTiming()
	{
		var dir = Directory;
		Assert.Equal(120, PitTiming.Load(dir).Ticket.TotalSeconds);
		using var pit = new Pit(dir, readOnly: false);
		new TextFile(dir, "Master", "flag").WriteAllText($"other|{DateTimeOffset.UtcNow:O}");
		Assert.False(TicketFixture.AttemptClaim(pit).Acquired);
		Configure(110, 15);
		Assert.Throws<FormatException>(() => PitTiming.Load(dir));
	}

	[Fact]
	[Trait("Category", "CloudEffect")]
	public async Task CollisionPreservesCanonicalAndPublishesDirty()
	{
		var dir = CloudDirectory();
		using var pit = new Pit(dir, readOnly: false);
		var canonical = new TextFile(pit.JsonFile.FullName);
		canonical.WriteAllText("[]");
		var before = canonical.ReadFromDisk();
		pit.Add(new PitItem("local"));
		var saving = pit.SaveWithOutcomeAsync();
		TicketFixture.WriteMaster(pit.MasterFlag(), originator: "remote-pits-1");
		var result = await saving;
		Assert.Equal(PitSaveOutcome.PublishedAsChanges, result.Outcome);
		Assert.Equal(1, result.FragmentCount);
		Assert.True(result.WaitedMs > 0);
		Assert.Equal(before, canonical.ReadAllText());
		Assert.Single(dir.EnumerateFiles("*.json"));
	}

	[Fact]
	[Trait("Category", "CloudEffect")]
	public async Task WinnerMergesArrivingCanonicalAndChangesAfterPropagation()
	{
		var dir = CloudDirectory();
		using var pit = new Pit(dir, readOnly: false);
		var canonical = new TextFile(pit.JsonFile.FullName);
		canonical.WriteAllText("[]");
		pit.Add(new PitItem("local"));
		var save = pit.SaveAsync();
		canonical.WriteAllText("""[[{"Id":"remote","Modified":"2026-01-01T00:00:00Z"}]]""");
		pit.CreateChangeFile(new PitItem("peer"), "Mdlaka-app-1");
		await save;
		var text = canonical.ReadAllText();
		foreach (var id in new[] { "remote", "local", "peer" }) Assert.Contains(id, text);
		Assert.Single(dir.EnumerateFiles("*.receipt"));
		Assert.Equal("Returned", pit.MasterFlag().ReadTicket().State);
	}

	[Fact]
	public async Task LocalCooldown_ExpiryAndReopen()
	{
		Configure(.8, 8);
		var dir = Directory;
		using (var first = new Pit(dir, readOnly: false))
		{
			first.Add(new PitItem("first"));
			Assert.Equal(0, (await first.SaveWithOutcomeAsync()).WaitedMs);
			first.MasterFlag().rm(); // Exercise the process watermark independently of the file.
		}
		using var reopened = new Pit(dir, readOnly: false);
		Assert.False(TicketFixture.AttemptClaim(reopened).Acquired);
		await Task.Delay(850, TestContext.Current.CancellationToken);
		Assert.True(TicketFixture.AttemptClaim(reopened).Acquired);
	}

	[Fact]
	[Trait("Category", "CloudEffect")]
	public void CloudFairness_RetainsMachineCooldownAndWait()
	{
		var dir = CloudDirectory();
		using var pit = new Pit(dir, readOnly: false);
		var now = DateTimeOffset.UtcNow;
		TicketFixture.WritePeerTicket(pit.MasterFlag(), new("Returned", "other-pid-42", now, now, Environment.MachineName));
		Assert.False(TicketFixture.AttemptClaim(pit).Acquired);
		TicketFixture.WritePeerTicket(pit.MasterFlag(), new("Returned", "remote-peer-42", now.AddHours(-1), now.AddHours(-1), "remote"));
		pit.Add(new PitItem("eligible"));
		var result = pit.SaveWithOutcome();
		Assert.Equal(PitSaveOutcome.Wrote, result.Outcome);
		Assert.True(result.WaitedMs > 0);
	}

	private sealed class ObservedPit(RaiPath dir, Action<MasterFlagFile> afterClaim) : Pit(dir, readOnly: false)
	{
		protected override void OnMasterAuthorityEvaluated(bool acquired, string owner, bool valid)
		{
			base.OnMasterAuthorityEvaluated(acquired, owner, valid);
			if (acquired) afterClaim(MasterFlag());
		}
	}

	[Fact]
	public async Task LocalTurnTaking_TwoProcesses()
	{
		const string workerPath = "CR095_LOCAL_PIT";
		if (Os.RunSwitch(workerPath) is string path)
		{
			var directory = new RaiPath(path);
			using var peer = new Pit(directory, readOnly: false);
			peer.Add(new PitItem("held"));
			var refused = peer.SaveWithOutcome();
			Assert.Equal(PitSaveOutcome.PublishedAsChanges, refused.Outcome);
			Assert.Equal(1, refused.FragmentCount);
			new TextFile(directory, "peer-refused", "ready").WriteAllText(peer.ExactProcessIdentity);
			var released = new RaiFile(directory, "retry", "ready");
			Assert.True(System.Threading.SpinWait.SpinUntil(() => released.Exists(), TimeSpan.FromSeconds(20)));
			peer.Add(new PitItem("released"));
			var won = peer.SaveWithOutcome();
			Assert.Equal(PitSaveOutcome.Wrote, won.Outcome);
			Assert.Equal(0, won.WaitedMs);
			return;
		}

		var dir = Directory;
		var runner = new RaiSystem("dotnet", new[] { typeof(CR090Tests).Assembly.Location, "-method",
			typeof(CR090Tests).FullName + "." + nameof(LocalTurnTaking_TwoProcesses) })
		{
			EnvironmentOverrides = new System.Collections.Generic.Dictionary<string, string> { [workerPath] = dir.FullPath }
		};
		Task<RaiSystemResult> peerRun = null;
		var refusedSignal = new TextFile(dir, "peer-refused", "ready");
		using var pit = new ObservedPit(dir, flag =>
		{
			var held = flag.ReadTicket();
			peerRun = Task.Run(() => runner.ExecResult(30000));
			Assert.True(System.Threading.SpinWait.SpinUntil(() => refusedSignal.Exists() || peerRun.IsCompleted,
				TimeSpan.FromSeconds(20)) && refusedSignal.Exists(), "Peer failed to publish during the held turn.");
			Assert.Equal(held, flag.ReadTicket());
		});
		pit.Add(new PitItem("owner"));
		var begin = DateTimeOffset.UtcNow;
		var saved = pit.SaveWithOutcome();
		Assert.Equal(PitSaveOutcome.Wrote, saved.Outcome);
		Assert.Equal(0, saved.WaitedMs);
		var returned = pit.MasterFlag().ReadTicket();
		Assert.Equal("Returned", returned.State);
		Assert.Equal(pit.ExactProcessIdentity, returned.Owner);
		Assert.Equal(Environment.MachineName, returned.Machine);
		Assert.InRange(returned.Claimed, begin, returned.Written.Value);
		Assert.InRange(returned.Written.Value, returned.Claimed, DateTimeOffset.UtcNow);
		pit.Add(new PitItem("deferred"));
		var retry = pit.SaveWithOutcome();
		Assert.Equal(PitSaveOutcome.PublishedAsChanges, retry.Outcome);
		Assert.Equal(0, retry.WaitedMs);
		Assert.Equal(returned, pit.MasterFlag().ReadTicket());

		new TextFile(dir, "retry", "ready").WriteAllText("ready");
		var result = await peerRun;
		Assert.True(result.Succeeded, result.Output);
		Assert.Equal(refusedSignal.ReadFromDisk().Trim(), pit.MasterFlag().ReadTicket().Owner);
	}

	[Fact]
	public void CleanSaveReturnsAndSelfBanSurvivesMissingFlag()
	{
		Configure(5, 0);
		var dir = Directory;
		using (var pit = new Pit(dir, readOnly: false))
		{
			pit.Add(new PitItem("local"));
			pit.Save();
			Assert.Equal("Returned", pit.MasterFlag().ReadTicket().State);
			Assert.False(TicketFixture.AttemptClaim(pit).Acquired);
			pit.MasterFlag().rm();
			Assert.False(TicketFixture.AttemptClaim(pit).Acquired);
		}
		Assert.Empty(dir.EnumerateFiles("*.json"));
	}
}
