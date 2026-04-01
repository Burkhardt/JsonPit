using System;
using System.IO;
using System.Linq;
using OsLib;
using Xunit;

namespace JsonPit.Tests
{
	public sealed class PitChangeMergeTests
	{
		private static RaiPath NewTestRoot(string testName)
		{
			var root = new RaiPath(Path.GetTempPath()) / "RAIkeep" / "jsonpit-tests" / "change-merge" / SanitizeSegment(testName);
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
		public void CreateChangeFile_WritesCanonicalPitUnderChangesDirectory()
		{
			var root = NewTestRoot(nameof(CreateChangeFile_WritesCanonicalPitUnderChangesDirectory));
			root.mkdir();

			try
			{
				var pit = new Pit((root / "pit-store").Path, readOnly: false, autoload: false, backup: false);
				var peerItem = new PitItem("PeerItem");
				peerItem.SetProperty(new { Value = 126, CreatedBy = "Mzansi", Marker = "peer-marker" });

				pit.CreateChangeFile(peerItem, "ubuntu");

					var changePits = Directory.GetFiles(pit.ChangesDir.Path, "*.json", SearchOption.AllDirectories).OrderBy(x => x).ToArray();
					Assert.NotEmpty(changePits);
					Assert.Contains(changePits, file => file.EndsWith("_ubuntu.json", StringComparison.OrdinalIgnoreCase));
			}
			finally
			{
				Cleanup(root);
			}
		}

		[Fact]
		public void Reload_MergesNestedCanonicalChangePit_FromChangesDirectory()
		{
			var root = NewTestRoot(nameof(Reload_MergesNestedCanonicalChangePit_FromChangesDirectory));
			root.mkdir();

			try
			{
				var pitPath = (root / "pit-store").Path;
				var masterPit = new Pit(pitPath, readOnly: false, autoload: false, backup: false);
				var localItem = new PitItem("CloudItem");
				localItem.SetProperty(new { Value = 42, CreatedBy = "RAIkeep", Marker = "local-marker" });
				masterPit.Add(localItem);
				masterPit.Save(force: true);

				var peerItem = new PitItem("PeerItem");
				peerItem.SetProperty(new { Value = 126, CreatedBy = "Mzansi", Marker = "peer-marker" });
				masterPit.CreateChangeFile(peerItem, "ubuntu");

				var reloaded = new Pit(pitPath, readOnly: false, autoload: false, backup: false);
				reloaded.Load(undercover: true);

				Assert.NotNull(reloaded.Get("CloudItem"));
				Assert.Null(reloaded.Get("PeerItem"));
				Assert.True(reloaded.ForeignChangesAvailable());

				var changed = reloaded.Reload();

				Assert.True(changed);
				Assert.NotNull(reloaded.Get("PeerItem"));
				Assert.Equal(126, reloaded.Get("PeerItem")?["Value"]?.ToObject<int>());
				Assert.Equal("Mzansi", reloaded.Get("PeerItem")?["CreatedBy"]?.ToObject<string>());
			}
			finally
			{
				Cleanup(root);
			}
		}
	}
}