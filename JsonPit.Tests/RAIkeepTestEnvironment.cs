using System;
using System.IO;
using OsLib;

namespace JsonPit.Tests
{
	internal static class RAIkeepTestEnvironment
	{
		private static readonly string rootPath = ((new RaiPath(Path.GetTempPath())) / "RAIkeep" / "JsonPitTests" / "CloudRoot").Path;

		static RAIkeepTestEnvironment()
		{
			CleanupRoot();
			new RaiPath(rootPath).mkdir();
			AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupRoot();
		}

		internal static string RootPath => rootPath;

		internal static string CloudPath(params string[] segments)
		{
			var path = new RaiPath(RootPath);
			foreach (var segment in segments)
				path = path / segment;
			return path.Path;
		}

		internal static string CloudFile(params string[] segments)
		{
			if (segments == null || segments.Length == 0)
				throw new ArgumentException("At least one segment is required.", nameof(segments));

			var path = new RaiPath(RootPath);
			for (var i = 0; i < segments.Length - 1; i++)
				path = path / segments[i];

			return new RaiFile(path.Path + segments[^1]).FullName;
		}

		private static void CleanupRoot()
		{
			try
			{
				if (Directory.Exists(rootPath))
					new RaiFile(rootPath).rmdir(depth: 10, deleteFiles: true);
			}
			catch
			{
			}
		}
	}
}