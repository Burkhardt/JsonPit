using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using OsLib;
using Xunit;
namespace JsonPit.Tests
{
	public class JsonPitPersistenceTests : IDisposable
	{
		private readonly TmpFile _testFile;
		public JsonPitPersistenceTests()
		{
			var tempDir = RAIkeepTestEnvironment.RootPath / "Persistence";
			_testFile = new TmpFile(tempDir, "persistence_audit_test.tmp");
			_testFile.mkdir();
			if (_testFile.Exists())
				_testFile.rm();
		}
		[Fact]
		public void StoreAndLoad_PreservesRawHistoryFragments_DoesNotFlatten()
		{
			var pitA = new Pit(pitDirectory: _testFile.Path, readOnly: false, autoload: false);
			var time1 = DateTimeOffset.UtcNow.AddMinutes(-10);
			var fragment1 = new PitItem(new JObject
			{
				[nameof(PitItem.Id)] = "Sensor_1",
				[nameof(PitItem.Modified)] = time1,
				[nameof(PitItem.Deleted)] = false,
				["Temp"] = 72
			});
			var time2 = DateTimeOffset.UtcNow;
			var fragment2 = new PitItem(new JObject
			{
				[nameof(PitItem.Id)] = "Sensor_1",
				[nameof(PitItem.Modified)] = time2,
				[nameof(PitItem.Deleted)] = false,
				["Temp"] = 74
			});
			// Use AddHistorical: the test's intent is to verify that raw history
			// fragments with explicit timestamps round-trip through Save/Load
			// unchanged.  Pit.Add (live mutation) deliberately refreshes Modified
			// to UtcNow and would defeat the purpose of this test.
			pitA.AddHistorical(fragment1);
			pitA.AddHistorical(fragment2);
			pitA.Save(force: true);
			var persistedFileFullName = pitA.JsonFile.FullName;
			var textFile = new TextFile(persistedFileFullName);
			var rawJson = string.Join(Environment.NewLine, textFile.Read());
			var diskArray = JArray.Parse(rawJson);
			Assert.True(diskArray[0] is JArray, "The JSON on disk is flattened! It must be an array of history arrays.");
			Assert.Equal(2, ((JArray)diskArray[0]).Count);
			var pitB = new Pit(pitDirectory: RaiPath.SplitRaiPathAndName(persistedFileFullName).path, readOnly: true, autoload: true);
			Assert.True(pitB.ContainsKey("Sensor_1"));
			var historyStack = pitB.HistoricItems["Sensor_1"];
			Assert.Equal(2, historyStack.Count);
			// PitItems is newest-first: Items[0] is the most recent fragment.
			Assert.Equal(time2, historyStack.Items[0].Modified);
			Assert.Equal(time1, historyStack.Items[1].Modified);
		}
		[Fact]
		public void RenameId_MigratesStateAndTombstonesOldKey()
		{
			var pit = new Pit(pitDirectory: _testFile.Path, readOnly: false, autoload: false);
			var original = new PitItem("LegacyTicker");
			original.SetProperty(new { Price = 262.77, Exchange = "NASDAQ" });
			pit.Add(original);
			var renamed = pit.RenameId("LegacyTicker", "AAPL");
			Assert.True(renamed);
			Assert.False(pit.Contains("LegacyTicker"));
			Assert.True(pit.Contains("AAPL"));
			Assert.Null(pit["LegacyTicker"]);
			var newState = pit["AAPL"];
			Assert.NotNull(newState);
			Assert.Equal("AAPL", newState!["Id"]!.Value<string>());
			Assert.Equal(262.77, newState["Price"]!.Value<double>(), 5);
			Assert.Equal("NASDAQ", newState["Exchange"]!.Value<string>());
			Assert.True(pit.HistoricItems.ContainsKey("LegacyTicker"));
			var oldHistory = pit.HistoricItems["LegacyTicker"];
			// Newest-first: the tombstone (most recent fragment) sits at History[0].
			Assert.True(oldHistory.History.First().Deleted);
			var oldDeletedState = pit.Get("LegacyTicker", withDeleted: true);
			Assert.NotNull(oldDeletedState);
			Assert.True(oldDeletedState!["Deleted"]!.Value<bool>());
			pit.Save(force: true);
			var rawJson = string.Join(Environment.NewLine, new TextFile(pit.JsonFile.FullName).Read());
			var diskArray = JArray.Parse(rawJson);
			Assert.Equal(2, diskArray.Count);
			Assert.Contains(diskArray.OfType<JArray>(), history => history.Any(fragment => fragment["Id"]?.Value<string>() == "LegacyTicker"));
			Assert.Contains(diskArray.OfType<JArray>(), history => history.Any(fragment => fragment["Id"]?.Value<string>() == "AAPL"));
		}
		public void Dispose()
		{
			if (_testFile.Exists())
				_testFile.rm();
		}
	}
}
