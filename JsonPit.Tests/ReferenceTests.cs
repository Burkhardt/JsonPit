using Newtonsoft.Json.Linq;
using Xunit;

namespace JsonPit.Tests;

public class ReferenceTests
{
	/// <summary>
	/// Both indexers return the original objects, so edits through their references
	/// are visible through subsequent lookups in the Pit.
	/// </summary>
	[Fact]
	public void ArrayOperator_ReturnsAddedItem()
	{
		using var pit = new Pit(
			RAIkeepTestEnvironment.CloudPath(nameof(ArrayOperator_ReturnsAddedItem)),
			readOnly: false,
			autoload: false);

		var who = new JObject { ["Owner"] = "Unknown" };
		var item = new PitItem("Ego") { ["Who"] = who };
		pit.Add(item);

		var ego = pit["Ego"];
		Assert.Same(item, ego);
		Assert.Same(who, ego["Who"]);

		ego["Instagram"] = "@Dr2RAI";
		ego["Who"]["Owner"] = "Rainer";

		Assert.Equal("@Dr2RAI", (string)item["Instagram"]);
		Assert.Equal("Rainer", (string)who["Owner"]);
		Assert.Same(ego, pit["Ego"]);
		Assert.Same(who, pit["Ego"]["Who"]);
	}
}
