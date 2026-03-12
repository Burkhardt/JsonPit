using System;
using System.Threading;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JsonPit.Tests
{
	public sealed class PitItem_SetProperty_ChangeDetection_Tests
	{
		[Fact]
		public void SetProperty_SameValue_DoesNotChangeModified()
		{
			// Arrange
			var item = new PitItem("AAPL");

			// First set: must change
			item.SetProperty(new { Price = 262.77 });
			var modified1 = item.Modified;

			// Give the clock a chance to advance (avoid false positives)
			Thread.Sleep(25);

			// Act: set identical value again
			item.SetProperty(new { Price = 262.77 });
			var modified2 = item.Modified;

			// Assert: Modified unchanged
			Assert.Equal(modified1, modified2);
			Assert.Equal(262.77, item["Price"]!.Value<double>(), 5);
		}

		[Fact]
		public void SetProperty_NewProperty_ChangesModified_WhileKeepingExisting()
		{
			// Arrange
			var item = new PitItem("AAPL");
			item.SetProperty(new { Price = 262.77 });
			var modified1 = item.Modified;

			Thread.Sleep(25);

			// Act: Price unchanged, Volume new => should count as change
			item.SetProperty(new { Price = 262.77, Volume = 17320 });
			var modified2 = item.Modified;

			// Assert
			Assert.True(modified2 > modified1, "Expected Modified to advance when at least one property changes.");
			Assert.Equal(262.77, item["Price"]!.Value<double>(), 5);
			Assert.Equal(17320, item["Volume"]!.Value<int>());
		}

		[Fact]
		public void SetProperty_ValueChanged_ChangesModified()
		{
			// Arrange
			var item = new PitItem("AAPL");
			item.SetProperty(new { Bid = 263.41 });
			var modified1 = item.Modified;

			Thread.Sleep(25);

			// Act: change Bid
			item.SetProperty(new { Bid = 263.43 });
			var modified2 = item.Modified;

			// Assert
			Assert.True(modified2 > modified1, "Expected Modified to advance when a value changes.");
			Assert.Equal(263.43, item["Bid"]!.Value<double>(), 5);
		}

		[Fact]
		public void SetProperty_NestedObject_SameValue_DoesNotChangeModified()
		{
			// Arrange
			var item = new PitItem("AAPL");
			item.SetProperty(@"{ ""Meta"": { ""src"": ""Tiingo"", ""venue"": ""IEX"" } }");
			var modified1 = item.Modified;

			Thread.Sleep(25);

			// Act: assign same nested object again
			item.SetProperty(@"{ ""Meta"": { ""src"": ""Tiingo"", ""venue"": ""IEX"" } }");
			var modified2 = item.Modified;

			// Assert
			Assert.Equal(modified1, modified2);

			var meta = item["Meta"] as JObject;
			Assert.NotNull(meta);
			Assert.Equal("Tiingo", meta!["src"]!.Value<string>());
			Assert.Equal("IEX", meta["venue"]!.Value<string>());
		}
	}
} // namespace JsonPit.Tests