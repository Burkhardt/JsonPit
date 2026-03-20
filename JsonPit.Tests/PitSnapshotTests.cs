using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using OsLib;
using Xunit;

namespace JsonPit.Tests
{
	public sealed class Pit_Snapshot_Tests
	{
		private static RaiPath NewTestRoot([CallerMemberName] string testName = "")
		{
			var root = new RaiPath(Os.TempDir) / "RAIkeep" / "jsonpit-tests" / "snapshot" / SanitizeSegment(testName);
			Cleanup(root);
			return root;
		}

		private static void Cleanup(RaiPath root)
		{
			try
			{
				if (Directory.Exists(root.Path))
					new RaiFile(root.Path).rmdir(depth: 10, deleteFiles: true);
			}
			catch
			{
			}
		}

		private static string SanitizeSegment(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
				return "test";

			var invalid = Path.GetInvalidFileNameChars();
			var cleaned = new string(value
				.Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '-' : ch)
				.ToArray())
				.Trim('-');

			return string.IsNullOrWhiteSpace(cleaned) ? "test" : cleaned;
		}

		[Fact]
		public void Constructor_WithFlatObjectArray_UsingId_LoadsCurrentItems()
		{
			var root = NewTestRoot();
			root.mkdir();

			try
			{
				var snapshot = JArray.FromObject(new object[]
				{
					new
					{
						Id = "Max",
						Email = "max@example.org",
						Phone = "+27-82-000-0000"
					},
					new
					{
						Id = "Rainer",
						Email = "rainer@africastage.com",
						Instagram = "Dr2RAI"
					}
				});

				var pit = new Pit(snapshot, root.Path, readOnly: false, autoload: false, backup: false);

				Assert.Equal(2, pit.Keys.Count);
				Assert.Equal("max@example.org", pit["Max"]?["Email"]?.Value<string>());
				Assert.Equal("Dr2RAI", pit["Rainer"]?["Instagram"]?.Value<string>());
			}
			finally
			{
				Cleanup(root);
			}
		}

		[Fact]
		public void Constructor_WithLegacyNameArray_MapsIdentityToId()
		{
			var root = NewTestRoot();
			root.mkdir();

			try
			{
				var legacySnapshot = JArray.FromObject(new object[]
				{
					new
					{
						Name = "Max",
						Email = "max@example.org"
					}
				});

				var pit = new Pit(legacySnapshot, root.Path, readOnly: false, autoload: false, backup: false);

				Assert.Single(pit.Keys);
				Assert.Equal("Max", pit["Max"]?["Id"]?.Value<string>());
				Assert.Null(pit["Max"]?["Name"]);
			}
			finally
			{
				Cleanup(root);
			}
		}

		[Fact]
		public void ExportJson_WritesFlatSnapshot_AtRequestedMoment()
		{
			var root = NewTestRoot();
			root.mkdir();

			try
			{
				var pit = new Pit(root.Path, readOnly: false, autoload: false, backup: false);

				var maxV1 = new PitItem("Max");
				maxV1.SetProperty(new { Email = "max-v1@example.org", Stage = "draft" });
				pit.Add(maxV1);
				var at = maxV1.Modified.AddTicks(1);

				System.Threading.Thread.Sleep(25);

				var maxV2 = new PitItem(maxV1);
				maxV2.SetProperty(new { Email = "max-v2@example.org", Stage = "live" });
				pit.Add(maxV2);

				var obsolete = new PitItem("Obsolete");
				obsolete.SetProperty(new { State = "active" });
				pit.Add(obsolete);

				System.Threading.Thread.Sleep(25);
				var deletedObsolete = new PitItem(obsolete);
				deletedObsolete.Delete();
				pit.Add(deletedObsolete);

				var exportFile = new RaiFile(root, "person-export", "json");
				pit.ExportJson(exportFile.FullName, at: at, pretty: true);

				var exported = JArray.Parse(File.ReadAllText(exportFile.FullName));

				Assert.NotEmpty(exported);
				Assert.All(exported, token => Assert.IsType<JObject>(token));
				Assert.DoesNotContain(exported, token => token is JArray);
				Assert.Equal("max-v1@example.org", exported.Single(obj => obj["Id"]!.Value<string>() == "Max")["Email"]!.Value<string>());
				Assert.DoesNotContain(exported, obj => obj["Id"]!.Value<string>() == "Obsolete");

				pit.ExportJson(exportFile.FullName, pretty: false);
				exported = JArray.Parse(File.ReadAllText(exportFile.FullName));

				Assert.Equal("max-v2@example.org", exported.Single(obj => obj["Id"]!.Value<string>() == "Max")["Email"]!.Value<string>());
				Assert.DoesNotContain(exported, obj => obj["Id"]!.Value<string>() == "Obsolete");
			}
			finally
			{
				Cleanup(root);
			}
		}
	}
}
