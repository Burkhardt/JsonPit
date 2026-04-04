using System;
using System.IO;
using OsLib;

namespace JsonPit.Tests
{
	internal static class RAIkeepTestEnvironment
	{
		private static readonly RaiPath rootPath = ((new RaiPath(Path.GetTempPath())) / "RAIkeep" / "JsonPitTests" / "CloudRoot");

		static RAIkeepTestEnvironment()
		{
			CleanupRoot();
			rootPath.mkdir();
			AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupRoot();
		}

		internal static RaiPath RootPath => rootPath;

		internal static RaiPath CloudPath(params string[] segments)
		{
			var path = RootPath;
			foreach (var segment in segments)
				path = path / segment;
			return path;
		}

		internal static string CloudFile(params string[] segments)
		{
			if (segments == null || segments.Length == 0)
				throw new ArgumentException("At least one segment is required.", nameof(segments));

			var path = RootPath;
			for (var i = 0; i < segments.Length - 1; i++)
				path = path / segments[i];

			return new RaiFile(path / segments[^1]).FullName;
		}

		private static void CleanupRoot()
		{
			try
			{
				if (rootPath.Exists())
					rootPath.rmdir(depth: 10, deleteFiles: true);
			}
			catch
			{
			}
		}
	}
}