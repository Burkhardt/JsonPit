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
	public void Dispose() => new RaiFile(root.Path).rmdir(depth: 6, deleteFiles: true);

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
		Assert.InRange(claim.EarliestWriteUtc, start.AddSeconds(40), DateTimeOffset.UtcNow.AddSeconds(40));
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
	[InlineData("Mzansi", 20, 45)]
	[InlineData("Mdlaka", 90, 20)]
	[InlineData("Nkosikazi", 70, 20)]
	[InlineData("Mzansi", 70, 20)]
	[InlineData("Mzansi", -90, 65)]
	public void ReturnedPropagationWindow(string previousMachine, int age, int expected)
	{
		using var pit = new Pit(Directory, readOnly: false);
		var now = DateTimeOffset.UtcNow;
		pit.MasterFlag().WriteTicket(new("Returned", "remote-app-1", now.AddMinutes(-3), now.AddSeconds(-age), previousMachine + "-fixture"));
		var turn = TicketFixture.AttemptClaim(pit);
		Assert.True(turn.Acquired);
		Assert.InRange((turn.EarliestWriteUtc - now).TotalSeconds, expected - .01, expected + 1);
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
	public async Task CollisionPreservesCanonicalAndPublishesDirty()
	{
		Configure(0, .25);
		var dir = Directory;
		var canonical = new TextFile(dir, "Person", "pit", "[]");
		var before = canonical.ReadAllText();
		using var pit = new Pit(dir, readOnly: false);
		pit.Add(new PitItem("local"));
		var saving = pit.SaveWithOutcomeAsync();
		TicketFixture.WriteMaster(pit.MasterFlag(), originator: "remote-pits-1");
		Assert.Equal(new(PitSaveOutcome.PublishedAsChanges, 1), await saving);
		Assert.Equal(before, canonical.ReadAllText());
		Assert.Single(dir.EnumerateFiles("*.json"));
	}

	[Fact]
	public async Task WinnerMergesArrivingCanonicalAndChangesAfterPropagation()
	{
		Configure(0, .2);
		var dir = Directory;
		var canonical = new TextFile(dir, "Person", "pit", "[]");
		using var pit = new Pit(dir, readOnly: false);
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
