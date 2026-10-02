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
				if (root.Exists())
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
		public void CreateChangeFile_WritesValidatedFragmentBesideCanonicalPit()
		{
			var root = NewTestRoot(nameof(CreateChangeFile_WritesValidatedFragmentBesideCanonicalPit));
			root.mkdir();
			try
			{
				using var pit = new Pit(root / "pit-store", readOnly: false, autoload: false, backup: false);
				var peerItem = new PitItem("PeerItem");
				peerItem.SetProperty(new { Value = 126, CreatedBy = "Mzansi", Marker = "peer-marker" });
				var file = pit.CreateChangeFile(peerItem, "ubuntu-tests-4242");
				var (payload, sha) = ChangeFile.CanonicalPayloadFor(peerItem);
				Assert.Equal($"{peerItem.Modified.UtcTicks}_ubuntu-tests-4242_{sha[..4]}.json", file.NameWithExtension);
				Assert.Equal(pit.JsonFile.Path.ToString(), file.Path.ToString());
				Assert.Equal(payload, File.ReadAllText(file.FullName));
				var stored = Assert.Single(ChangeFile.ReadValidated(file));
				Assert.Equal("PeerItem", (string)stored["Id"]);
				Assert.Equal(126, (int)stored["Value"]);
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
				var pitPath = (root / "pit-store");
				var masterPit = new Pit(pitPath, readOnly: false, autoload: false, backup: false);
				var localItem = new PitItem("CloudItem");
				localItem.SetProperty(new { Value = 42, CreatedBy = "RAIkeep", Marker = "local-marker" });
				masterPit.Add(localItem);
				masterPit.Save(force: true);
				var peerItem = new PitItem("PeerItem");
				peerItem.SetProperty(new { Value = 126, CreatedBy = "Mzansi", Marker = "peer-marker" });
				masterPit.CreateChangeFile(peerItem, "ubuntu-tests-4242");
				masterPit.Dispose(); // release canonical-path ownership before reopening (CR003 §4)
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
