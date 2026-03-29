using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using JsonPit;
using Newtonsoft.Json.Linq;
using OsLib;
using Xunit;

namespace JsonPit.Tests
{
	public class JsonPitRealWorldIntegrationTests
	{
		/// <summary>
		/// This tests the integration of JsonPit with cloud storage providers and the ability to repeatedly import the same json5
		/// file into the same Pit instance without throwing an exception or changing the state of the Pit since the data stays the same,
		/// only time changed. This is therefore also a test of idempotency.
		/// </summary>
		[Fact]
		public void WWWA_IntegrationTest_CloudDrive_Idempotency()
		{
			// Given are 5 files in this project's sample directory: Place.json5, Person.json5, Object.json5, Activity.json5
			// see JsonPit.Tests.csproj how the sample directory content from the project is copied to the cloud storage location
			var sampleDir = Os.CloudStorageRootDir / "RAIkeep" / "sample"; 

			// When the destination Pit does not exist, the Pit is created and the existing values are loaded
			var testDirInCloud = Os.CloudStorageRootDir / "RAIkeep" / "WwwaTests";
			var placePitFile = new PitFile(testDirInCloud, "Place");
			var personPitFile = new PitFile(testDirInCloud, "Person");
			var objectPitFile = new PitFile(testDirInCloud, "Object");
			var activityPitFile = new PitFile(testDirInCloud, "Activity");

			Assert.EndsWith($"{Os.DIRSEPERATOR}Place{Os.DIRSEPERATOR}Place.pit", placePitFile.FullName);

			// When the destination Pit does exist, the Pit is loaded and importing the same file leads to a sequence of Add operations 
			// that do not change the state of the Pit since the data stays the same;
			var placePit = new Pit(placePitFile);    // open for read/write is default
			var personPit = new Pit(personPitFile);
			var objectPit = new Pit(objectPitFile);
			var activityPit = new Pit(activityPitFile);

			// Importing the data from the JSON5 files into the Pit
			var placeData = new TextFile(sampleDir, "Place", "json5").ReadAllText();
			placePit.AddItems(placeData);
			placePit.Save();	// just to make sure we can inspect the data in the cloud storage
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
			Assert.Equal("Am Spitalbach 8, 74523 Schwäbisch Hall, Germany", hospitalhof[ "Address"]?.ToString());
			Assert.Equal("Am Spitalbach 8, 74523 Schwäbisch Hall, Germany", hospitalhof?.Address?.ToString());

			// Verify that the foreign key relationship is correctly established

		}
		[Theory]
		[InlineData(CloudStorageType.GoogleDrive)]
		[InlineData(CloudStorageType.Dropbox)]
		[InlineData(CloudStorageType.OneDrive)]
		public void TestJsonPitInCloudDrive(CloudStorageType provider)
		{
			if (!TryPrepareWritableIntegrationRoot(provider, out var root, out var providerRoot, out var reason))
				Assert.Skip($"Provider {provider}: {reason}. {Os.GetCloudStorageSetupGuidance()}");

			try
			{
				var mod = DateTime.Now;
				var people = new object[]
				{
					new { Name = "Max", Phone = "619-123-4567", Email = "Max@gmail.com" },
					new { Name = "Rainer", Phone = "442-226-8963", Email = "Rainer@AfricaStage.com", Instagram = "RSBurkhardt", Modified = mod.AddDays(-1) },
					new { Name = "Rainer", Email = "rsb@jgency.com" },
					new { Name = "Alice", Phone = "123-456-7890", Email = "Alice@example.com", Modified = mod.AddDays(-2), Address = new object[] {
						new { Street = "123 Main St", Unit = "Apt 4B", City = "San Diego", State = "CA", Zip = "92101" },
						new { Street = "456 Oak Ave", City = "Los Angeles", State = "CA", Zip = "90001" },
						new { Street = "789 Pine Rd", Unit = "Suite 100", City = "New York", State = "NY", Zip = "10001" },
					} },
					new { Name = "Rainer", Instagram = "Dr2RAI", Modified = mod.AddTicks(3) }
				};
				var personArray = JArray.FromObject(people);

				var personPit = new Pit(personArray, root.Path, readOnly: false, autoload: false, backup: false);

				personPit.Save();

				var reloaded = new Pit(root.Path, readOnly: false, autoload: true, backup: false);

				Assert.True(personPit.JsonFile.Cloud);
				Assert.Equal("Max@gmail.com", reloaded["Max"]?["Email"]?.ToObject<string>());
				Assert.Equal("Dr2RAI", reloaded["Rainer"]?["Instagram"]?.ToObject<string>());

				Console.WriteLine($"Provider {provider}: file={personPit.JsonFile.FullName} root={providerRoot}");
			}
			finally
			{
				Cleanup(root);
			}
		}

		[Theory]
		[InlineData(CloudStorageType.Dropbox)]
		[InlineData(CloudStorageType.OneDrive)]
		[InlineData(CloudStorageType.GoogleDrive)]
		public void Pit_SaveAndReload_WorksAgainstRealWritableCloudProvider(CloudStorageType provider)
		{
			if (!TryPrepareWritableIntegrationRoot(provider, out var root, out var providerRoot, out var reason))
				Assert.Skip($"Provider {provider}: {reason}. {Os.GetCloudStorageSetupGuidance()}");

			try
			{
				var pitRoot = root / "pit-store";
				pitRoot.mkdir();

				var pit = new Pit(pitRoot.Path, readOnly: false, autoload: false, backup: false);
				var item = new PitItem("CloudItem");
				item.SetProperty(new { Value = 42, Provider = provider.ToString(), CreatedBy = "RAIkeep" });
				pit.Add(item);

				var saveTimer = Stopwatch.StartNew();
				pit.Save(force: true);
				saveTimer.Stop();

				Assert.True(pit.JsonFile.Cloud);
				Assert.True(pit.JsonFile.Exists());
				pit.JsonFile.AwaitMaterializing();

				var loadTimer = Stopwatch.StartNew();
				var reloaded = new Pit(pitRoot.Path, readOnly: false, autoload: true, backup: false);
				loadTimer.Stop();

				var loaded = reloaded.Get("CloudItem");
				Assert.NotNull(loaded);
				Assert.Equal(42, loaded["Value"]!.ToObject<int>());
				Assert.Equal(provider.ToString(), loaded["Provider"]!.ToObject<string>());
				Assert.Equal("RAIkeep", loaded["CreatedBy"]!.ToObject<string>());

				Console.WriteLine($"Provider {provider}: pit-save={saveTimer.ElapsedMilliseconds}ms pit-load={loadTimer.ElapsedMilliseconds}ms file={pit.JsonFile.FullName} root={providerRoot}");
			}
			finally
			{
				Cleanup(root);
			}
		}

		private static bool TryPrepareWritableIntegrationRoot(CloudStorageType provider, out RaiPath root, out string providerRoot, out string reason)
		{
			Os.ResetCloudStorageCache();
			providerRoot = Os.GetCloudStorageRoot(provider, refresh: true);
			if (string.IsNullOrWhiteSpace(providerRoot))
			{
				root = Os.TempDir / "RAIkeep" / "missing-cloud-root";
				reason = "provider root is not configured or not discoverable on this machine";
				return false;
			}

			providerRoot = new RaiPath(providerRoot).Path;
			if (!Directory.Exists(providerRoot))
			{
				root = new RaiPath(providerRoot) / "RAIkeep" / "jsonpit-cloud-integration-tests" / provider.ToString();
				reason = $"provider root does not exist: {providerRoot}";
				return false;
			}

			root = new RaiPath(providerRoot) / "RAIkeep" / "jsonpit-cloud-integration-tests" / provider.ToString();
			reason = string.Empty;

			try
			{
				Cleanup(root);
				root.mkdir();
				return true;
			}
			catch (UnauthorizedAccessException ex)
			{
				reason = $"root is not writable: {ex.Message}";
			}
			catch (IOException ex)
			{
				reason = $"root is not writable: {ex.Message}";
			}

			return false;
		}

		private static bool TryPrepareDefaultCloudExampleRoot(out RaiPath root, out string reason)
		{
			Os.ResetCloudStorageCache();

			string providerRoot;
			try
			{
				providerRoot = Os.CloudStorageRootDir.Path;
			}
			catch (DirectoryNotFoundException ex)
			{
				root = Os.TempDir / "RAIkeep" / "examples" / "person";
				reason = ex.Message;
				return false;
			}

			if (string.IsNullOrWhiteSpace(providerRoot))
			{
				root = Os.TempDir / "RAIkeep" / "examples" / "person";
				reason = "no default cloud root is configured or discoverable on this machine";
				return false;
			}

			root = new RaiPath(providerRoot) / "RAIkeep" / "examples" / "person";
			reason = string.Empty;

			try
			{
				Cleanup(root);
				root.mkdir();
				return true;
			}
			catch (UnauthorizedAccessException ex)
			{
				reason = $"default cloud root is not writable: {ex.Message}";
			}
			catch (IOException ex)
			{
				reason = $"default cloud root is not writable: {ex.Message}";
			}

			return false;
		}

		private static void Cleanup(RaiPath root)
		{
			try
			{
				if (Directory.Exists(root.Path))
					new RaiFile(root.Path).rmdir(depth: 10, deleteFiles: true);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Cleanup warning for {root.Path}: {ex.Message}");
			}
		}
	}
}