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
			var tempDir = Os.TempDir / "TestArtifacts";
			_testFile = new TmpFile("persistence_audit_test.tmp") { Path = tempDir.Path };

			_testFile.mkdir();

			if (_testFile.Exists())
				_testFile.rm();
		}

		[Fact]
		public void StoreAndLoad_PreservesRawHistoryFragments_DoesNotFlatten()
		{
			var pitA = new Pit(pitDirectory: _testFile.FullName, readOnly: false, autoload: false);

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

			pitA.Add(fragment1);
			pitA.Add(fragment2);

			pitA.Save(force: true);

			var persistedPath = pitA.JsonFile.FullName;
			var textFile = new TextFile(persistedPath);
			var rawJson = string.Join(Environment.NewLine, textFile.Read());

			var diskArray = JArray.Parse(rawJson);
			Assert.True(diskArray[0] is JArray, "The JSON on disk is flattened! It must be an array of history arrays.");
			Assert.Equal(2, ((JArray)diskArray[0]).Count);

			var pitB = new Pit(pitDirectory: persistedPath, readOnly: true, autoload: true);

			Assert.True(pitB.ContainsKey("Sensor_1"));
			var historyStack = pitB.HistoricItems["Sensor_1"];

			Assert.Equal(2, historyStack.Count);
			Assert.Equal(time1, historyStack.Items[0].Modified);
			Assert.Equal(time2, historyStack.Items[1].Modified);
		}

		[Fact]
		public void RenameId_MigratesStateAndTombstonesOldKey()
		{
			var pit = new Pit(pitDirectory: _testFile.FullName, readOnly: false, autoload: false);

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
			Assert.True(oldHistory.History.Last().Deleted);

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
