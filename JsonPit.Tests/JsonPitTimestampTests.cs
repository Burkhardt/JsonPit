using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;
using JsonPit;

namespace JsonPit.Tests
{
	public class JsonPitTimestampTests
	{
		[Fact]
		public async Task ExtendWith_UpdatesModifiedTimestamp_OnlyWhenDataChanges()
		{
			// Arrange: Create a new item and capture its exact creation time
			string originalJson = "{ 'Settings': { 'Theme': 'Dark' } }";
			var pitItem = new PitItem("Rainer", originalJson);
			DateTimeOffset originalTime = pitItem.Modified;

			// Force a tiny delay so the system clock ticks forward.
			// This ensures the new timestamp will be demonstrably greater if updated.
#pragma warning disable xUnit1051 // Calls to methods which accept CancellationToken should use TestContext.Current.CancellationToken
			await Task.Delay(50);
#pragma warning restore xUnit1051 // Calls to methods which accept CancellationToken should use TestContext.Current.CancellationToken

			// Act 1: Attempt to merge the exact same data
			JObject identicalUpdate = JObject.Parse("{ 'Settings': { 'Theme': 'Dark' } }");
			bool didChangeIdentical = pitItem.ExtendWith(identicalUpdate);

			// Assert 1: The merge should detect no changes, and the timestamp must stay untouched
			Assert.False(didChangeIdentical, "ExtendWith returned true even though the data was identical.");
			Assert.Equal(originalTime, pitItem.Modified);

			// Act 2: Introduce a real change
			JObject realUpdate = JObject.Parse("{ 'Settings': { 'Theme': 'Light' } }");
			bool didChangeReal = pitItem.ExtendWith(realUpdate);

			// Assert 2: The merge should succeed, and Invalidate() should have bumped the timestamp
			Assert.True(didChangeReal, "ExtendWith returned false even though the data was changed.");
			Assert.True(pitItem.Modified > originalTime, "Modified timestamp was not updated after a successful deep merge.");
		}
	}
}