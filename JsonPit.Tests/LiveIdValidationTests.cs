using System;
using Newtonsoft.Json.Linq;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

public sealed class LiveIdValidationTests
{
    [Theory]
    [InlineData("{AdminPersonId}")]
    [InlineData("<Pending>")]
    [InlineData("Person{Suffix}")]
    [InlineData("Activity<Pending")]
    [InlineData("")]
    [InlineData(" ")]
    public void LiveWritesRejectInvalidIdsWithoutMutation(string id)
    {
        using var pit = NewPit();
        var item = new PitItem(id);
        Assert.Throws<ArgumentException>(() => pit.Add(item));
        Assert.Throws<ArgumentException>(() => pit.PitItem = item);
        Assert.Throws<ArgumentException>(() => pit.AddItems(new[] { new PitItem("Good"), item }));
        Assert.Throws<ArgumentException>(() => pit.AddItems(new JArray(new JObject { ["Id"] = "Good" }, new JObject { ["Id"] = id }).ToString()));
        Assert.Empty(pit.HistoricItems);
    }

    [Theory]
    [InlineData("{ 'Name': 'NotAnId' }")]
    [InlineData("{ 'Id': 42 }")]
    [InlineData("{ 'Id': null }")]
    public void LiveWritesDoNotInventOrCoerceIds(string json)
    {
        using var pit = NewPit();
        Assert.Throws<ArgumentException>(() => pit.Add(json));
        Assert.Throws<ArgumentException>(() => pit.Add(new PitItem(JObject.Parse(json))));
        Assert.Empty(pit.HistoricItems);
    }

    [Fact]
    public void OrdinaryFieldsAndUnicodeIdsRemainUnrestricted()
    {
        using var pit = NewPit();
        Assert.True(pit.Add("{ 'Id': 'München}', 'Name': '{Name}', 'Note': '<tag>', 'Nested': { 'Id': '{domain value}' } }"));
        Assert.Equal("<tag>", pit.Get("München}")["Note"]!.Value<string>());
    }

    [Fact]
    public void HistoricalReplayAndExplicitDeletionWorkButLiveRecreationFails()
    {
        using var pit = NewPit();
        var old = new PitItem("{Legacy}", true, DateTimeOffset.UtcNow.AddDays(-1));
        Assert.True(pit.AddHistorical(old));
        Assert.NotNull(pit.Get("{Legacy}"));
        Assert.True(pit.Delete("{Legacy}", backDate: false));
        Assert.Null(pit.Get("{Legacy}"));
        Assert.True(pit.Get("{Legacy}", withDeleted: true)["Deleted"]!.Value<bool>());
        Assert.Throws<ArgumentException>(() => pit.Add(new PitItem("{Legacy}")));
        Assert.False(pit.Delete("{NeverExisted}"));
        Assert.False(pit.Contains("{NeverExisted}", withDeleted: true));
    }

    [Fact]
    public void InvalidRenameDoesNotDeleteOriginal()
    {
        using var pit = NewPit();
        pit.Add(new PitItem("Good"));
        Assert.Throws<ArgumentException>(() => pit.RenameId("Good", "<Bad>"));
        Assert.True(pit.Contains("Good"));
    }

    private static Pit NewPit() => new(Os.TempDir / "RAIkeep" / "cr049" / Guid.NewGuid().ToString("N"), readOnly: true, unflagged: true, autoload: false);
}
