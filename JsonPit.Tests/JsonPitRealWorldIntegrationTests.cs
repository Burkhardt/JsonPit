using System;
using JsonPit;
using OsLib;
using Xunit;
namespace JsonPit.Tests
{
	public class JsonPitRealWorldIntegrationTests
	{
		/// <summary>
		/// This tests the integration of JsonPit with local storage and the ability to repeatedly import the same json5
		/// file into the same Pit instance without throwing an exception or changing the state of the Pit since the data stays the same,
		/// only time changed. This is therefore also a test of idempotency.
		/// </summary>
		[Fact]
		public void WWWA_IntegrationTest_LocalImport()
		{
			var testRootName = "WwwaTests-" + Guid.NewGuid().ToString("N");
			var testDir = RAIkeepTestEnvironment.LocalPath(testRootName).mkdir();

			try
			{
				var sampleDir = GetLocalSampleDirectory();
				var placePitFile = new PitFile(testDir, "Place");
				var personPitFile = new PitFile(testDir, "Person");
				var objectPitFile = new PitFile(testDir, "Object");
				var activityPitFile = new PitFile(testDir, "Activity");
				Assert.EndsWith($"{Os.DIR}Place{Os.DIR}Place.pit", placePitFile.FullName);
				// When the destination Pit does exist, the Pit is loaded and importing the same file leads to a sequence of Add operations
				// that do not change the state of the Pit since the data stays the same;
				using var placePit = new Pit(placePitFile);    // open for read/write is default
				using var personPit = new Pit(personPitFile);
				using var objectPit = new Pit(objectPitFile);
				using var activityPit = new Pit(activityPitFile);
				// Importing the data from the JSON5 files into the Pit
				var placeData = new TextFile(sampleDir, "Place", "json5").ReadAllText();
				placePit.AddItems(placeData);
				placePit.Save();    // just to make sure we can inspect the data in the test directory
				var personData = new TextFile(sampleDir, "Person", "json5").ReadAllText();
				personPit.AddItems(personData);
				personPit.Save();
				var objectData = new TextFile(sampleDir, "Object", "json5").ReadAllText();
				objectPit.AddItems(objectData);
				objectPit.Save();
				var activityData = new TextFile(sampleDir, "Activity", "json5").ReadAllText();
				activityPit.AddItems(activityData);
				activityPit.Save();
				// Then several explicit tests for values directly taken from the input files
				// sample/Place.json5, sample/Person.json5, sample/Object.json5, sample/Activity.json5
				// Verify Person.json5 data is loaded
				dynamic nomsa = personPit["Nomsa"];
				Assert.NotNull(nomsa);
				Assert.Equal("Nomsa", nomsa.Id.ToString());
				var instruments = nomsa?.Instruments;
				Assert.Contains("Voice", instruments);
				Assert.Contains("Percussion", instruments);
				// Verify Place.json5 data is loaded
				dynamic safariPark = placePit["SDZSafariPark"];
				Assert.NotNull(safariPark);
				Assert.Equal("SDZSafariPark", safariPark?.Id?.ToString());
				Assert.Equal("https://sdzsafaripark.org/", safariPark?.Homepage?.ToString());
				// Verify Object.json5 data is loaded
				dynamic ticket = objectPit["Ticket_SDSU26"];
				Assert.NotNull(ticket);
				Assert.Equal("$10.00 – $15.00", ticket?.Price?.ToString());
				// Verify Activity.json5 data is loaded
				dynamic activity = activityPit["SDZSP26"];
				Assert.NotNull(activity);
				Assert.Equal("Nomsa performing in the Elephant Valley", activity?.Title?.ToString());
				Assert.Equal("March 5-8, 2026", activity?.ShowTime?.Date?.ToString());
				// Verify foreign keys
				dynamic africanPicnic25 = activityPit["AfricanPicnic25"];
				dynamic location = africanPicnic25?.Where?.Venue;
				dynamic hospitalhof = placePit["Hospitalhof"];
				Assert.NotNull(location);
				Assert.NotNull(hospitalhof);
				Assert.Equal("Am Spitalbach 8, 74523 Schwäbisch Hall, Germany", hospitalhof["Address"]?.ToString());
				Assert.Equal("Am Spitalbach 8, 74523 Schwäbisch Hall, Germany", hospitalhof?.Address?.ToString());
			}
			finally
			{
				testDir.rmdir(depth: 10, deleteFiles: true);
			}
		}
		private static RaiPath GetLocalSampleDirectory()
		{
			var sampleDir = new RaiPath(AppContext.BaseDirectory) / "sample";
			if (!sampleDir.Exists())
				throw new System.IO.DirectoryNotFoundException($"Expected sample fixtures under '{sampleDir.Path}'.");
			return sampleDir;
		}
	}
}
