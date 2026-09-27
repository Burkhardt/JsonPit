using System;
using Newtonsoft.Json.Linq;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

public sealed class ProtectedAttributeTests
{
	[Theory]
	[InlineData("Modified")]
	[InlineData("modified")]
	[InlineData("Deleted")]
	[InlineData("DELETED")]
	[InlineData("Id")]
	[InlineData("id")]
	public void SetProperty_ProtectedTopLevelAttribute_FailsAtomically(string attribute)
	{
		var item = new PitItem("Sipho");
		item.SetProperty(new { Role = "Musician" });
		var before = item.ToString();

		var error = Assert.Throws<ProtectedAttributeException>(() =>
			item.SetProperty(new JObject { ["Role"] = "Soloist", [attribute] = "client" }));

		Assert.Equal(attribute, error.AttributeName);
		Assert.Equal(before, item.ToString());
	}

	[Fact]
	public void ExtendArray_ProtectedAttributeInLaterRow_FailsBeforeAnyRowMutatesState()
	{
		var item = new PitItem("Sipho");
		var before = item.ToString();
		var rows = new JArray(
			new JObject { ["Role"] = "Musician" },
			new JObject { ["Deleted"] = true });

		Assert.Throws<ProtectedAttributeException>(() => item.ExtendWith(rows));
		Assert.Equal(before, item.ToString());
	}

	[Theory]
	[InlineData("Id")]
	[InlineData("modified")]
	[InlineData("DELETED")]
	[InlineData("Modified.Child")]
	public void Tombstone_ProtectedAttribute_IsRejected(string path)
	{
		var item = new PitItem("Sipho");
		var before = item.ToString();
		Assert.Throws<TombstoneException>(() => item.DeletePropertyPath(path));
		Assert.Equal(before, item.ToString());
	}

	[Fact]
	public void NestedDomainPropertyNamedModified_RemainsValid()
	{
		var item = new PitItem("Sipho");
		item.SetProperty(JObject.Parse("{ 'What': { 'Modified': 'domain value' } }"));
		Assert.Equal("domain value", item["What"]?["Modified"]?.Value<string>());
		item.DeletePropertyPath("What.Modified");
		Assert.Equal(JTokenType.Null, item["What"]?["Modified"]?.Type);
	}

	[Fact]
	public void LiveAdd_RejectsProjectedReadModifyWrite_WhileHistoricalReplayAcceptsIt()
	{
		var root = Os.TempDir / "RAIkeep" / "jsonpit-tests" / $"cr040-{Guid.NewGuid():N}";
		root.mkdir();
		try
		{
			using var pit = new Pit(root, readOnly: false, unflagged: true, autoload: false);
			var historicalTime = DateTimeOffset.Parse("2026-09-26T20:00:00+02:00");
			var payload = JObject.Parse(
				$"{{ 'Id': 'Sipho', 'Modified': '{historicalTime:O}', 'Deleted': false, 'Role': 'Musician' }}");
			var projectedPayload = new PitItem(payload);

			Assert.Throws<ProtectedAttributeException>(() => pit.Add(projectedPayload));
			Assert.False(pit.Contains("Sipho", withDeleted: true));

			Assert.True(pit.AddHistorical(new PitItem(payload)));
			Assert.Equal(historicalTime.UtcTicks, pit.HistoricItems["Sipho"].LatestFragment()!.Modified.UtcTicks);
		}
		finally
		{
			try { root.rmdir(depth: 5, deleteFiles: true); } catch { }
		}
	}
}
