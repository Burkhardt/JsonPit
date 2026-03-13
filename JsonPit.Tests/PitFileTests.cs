using System;
using System.IO;
using System.Linq;
using OsLib;
using Xunit;

namespace JsonPit.Tests
{
	public class PitFileTests
	{
		private static RaiPath NewTestRoot()
		{
			return new RaiPath(Os.TempDir) / "RAIkeep" / "jsonpit-tests" / "pitfile" / Guid.NewGuid().ToString("N");
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

		[Fact]
		public void GetAllFiles_ReturnsOnlyPitFiles()
		{
			var root = NewTestRoot();
			root.mkdir();

			try
			{
				var pit = new PitFile((root / "Portfolio").Path);
				var nested = new RaiPath(pit.Path) / "Nested";
				nested.mkdir();

				File.WriteAllText(new RaiFile(pit.Path + "extra.pit").FullName, "pit");
				File.WriteAllText(new RaiFile(nested.Path + "nested.pit").FullName, "pit");
				File.WriteAllText(new RaiFile(pit.Path + "sidecar.json").FullName, "json");
				File.WriteAllText(new RaiFile(pit.Path + "preview.png").FullName, "png");

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