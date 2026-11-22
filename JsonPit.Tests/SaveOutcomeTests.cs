using System;
using System.Linq;
using System.Threading.Tasks;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

public sealed class SaveOutcomeTests : IDisposable
{
	private readonly RaiPath root = Os.TempDir / Guid.NewGuid().ToString("N");
	private RaiPath PitDirectory => root / "Tenant" / "Person";

	public void Dispose() => root.rmdir(depth: 8, deleteFiles: true);

	[Fact]
	public async Task CanonicalWriteThenCleanSaveReportsActualDiskEffects()
	{
		using var pit = new Pit(PitDirectory, readOnly: false, unflagged: true);
		var file = pit.JsonFile;
		new TextFile(file.FullName, "[]");
		file.LastWriteTimeUtc = DateTimeOffset.UtcNow.AddHours(-1);
		var old = file.LastWriteTimeUtc;
		pit.Add(new PitItem("X1"));
		Assert.Equal(new(PitSaveOutcome.Wrote), await pit.SaveWithOutcomeAsync());
		Assert.False(pit.Invalid());
		Assert.True(file.LastWriteTimeUtc > old);
		var stamp = file.LastWriteTimeUtc;
		var bytes = file.ReadAllBytes();
		Assert.Contains("X1", new TextFile(file.FullName).ReadAllText());
		Assert.Equal(new(PitSaveOutcome.NothingToWrite), pit.SaveWithOutcome());
		Assert.Equal(stamp, file.LastWriteTimeUtc);
		Assert.Equal(bytes, file.ReadAllBytes());
	}

	[Fact]
	public async Task ForeignTicketReportsOnlyDurableFragments()
	{
		new TextFile(PitDirectory.mkdir(), "Person", "pit", "[]");
		using var pit = new Pit(PitDirectory, readOnly: false);
		var canonical = pit.JsonFile.ReadAllBytes();
		var stamp = pit.JsonFile.LastWriteTimeUtc;
		TicketFixture.WriteMaster(TicketFixture.PeerFlag(PitDirectory, "Remote"), originator: "Remote-app-123");
		Assert.Equal(new(PitSaveOutcome.NothingToWrite), pit.SaveWithOutcome(force: true));
		Assert.Empty(PitDirectory.EnumerateFiles("*.json"));
		pit.Add(new PitItem("X1"));
		pit.Add(new PitItem("X2"));
		Assert.Equal(new(PitSaveOutcome.PublishedAsChanges, 2), await pit.SaveWithOutcomeAsync());
		Assert.Equal(new[] { "X1", "X2" }, PitDirectory.EnumerateFiles("*.json")
			.Select(f => (string)ChangeFile.ReadValidated(f)[0]["Id"]).Order());
		Assert.False(pit.Invalid());
		Assert.Equal(stamp, pit.JsonFile.LastWriteTimeUtc);
		Assert.Equal(canonical, pit.JsonFile.ReadAllBytes());
	}

	[Fact]
	public void ExplicitLoadDistinguishesUnloadedFromEmpty()
	{
		new TextFile(PitDirectory.mkdir(), "Person", "pit", "[]");
		using var pit = new Pit(PitDirectory, readOnly: false, autoload: false);
		Assert.Equal(new(PitSaveOutcome.PitNotLoaded), pit.SaveWithOutcome());
		Assert.True(pit.Load());
		Assert.Equal(new(PitSaveOutcome.NothingToWrite), pit.SaveWithOutcome());
		Assert.Empty(PitDirectory.EnumerateFiles("*.flag"));
	}

	[Fact]
	public async Task ReadOnlyAlwaysThrows()
	{
		using var pit = new Pit(PitDirectory, readOnly: true);
		Assert.Throws<System.IO.IOException>(() => pit.SaveWithOutcome());
		await Assert.ThrowsAsync<System.IO.IOException>(() => pit.SaveWithOutcomeAsync());
		Assert.Throws<System.IO.IOException>(() => pit.Save());
	}
}
