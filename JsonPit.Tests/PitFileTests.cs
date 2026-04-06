using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using OsLib;
using Xunit;
namespace JsonPit.Tests
{
	public class PitFileTests
	{
		private static RaiPath NewTestRoot([CallerMemberName] string testName = "")
		{
			var root = new RaiPath(Path.GetTempPath()) / "RAIkeep" / "jsonpit-tests" / "pitfile" / SanitizeSegment(testName);
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
		public void GetAllFiles_ReturnsOnlyPitFiles()
		{
			var root = NewTestRoot();
			root.mkdir();
			try
			{
				var pit = new PitFile(root / "Portfolio", "TestPit");
				var nested = pit.Path / "Nested";
				nested.mkdir();
				new TextFile(new RaiFile(pit.Path + "extra.pit").FullName, "pit");
				new TextFile(new RaiFile(nested.Path + "nested.pit").FullName, "pit");
				new TextFile(new RaiFile(pit.Path + "sidecar.json").FullName, "json");
				new TextFile(new RaiFile(pit.Path + "preview.png").FullName, "png");
				var files = pit.GetAllFiles(pit.Path, excludeCanonicalFile: true).OrderBy(x => x).ToArray();
				Assert.Equal(2, files.Length);
				Assert.All(files, file => Assert.Equal(".pit", Path.GetExtension(file)));
				Assert.DoesNotContain(files, file => file.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
				Assert.DoesNotContain(files, file => file.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
			}
			finally
			{
				Cleanup(root);
			}
		}
	}
}
