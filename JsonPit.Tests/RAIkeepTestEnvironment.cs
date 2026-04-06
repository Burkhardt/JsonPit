using System;
using OsLib;
namespace JsonPit.Tests
{
	internal static class RAIkeepTestEnvironment
	{
		private static readonly RaiPath rootPath = ResolveWritableRootPath();
		static RAIkeepTestEnvironment()
		{
			CleanupRoot();
			rootPath.mkdir();
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
			return new RaiFile(path, segments[^1]).FullName;
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
		private static RaiPath ResolveWritableRootPath()
		{
			var baseRoot = Os.TempDir;
			var candidate = baseRoot / "RAIkeep" / "JsonPitTests" / "CloudRoot";
			try
			{
				candidate.mkdir();
				candidate.rmdir(depth: 3, deleteFiles: true);
				return candidate;
			}
			catch (Exception ex)
			{
				throw new InvalidOperationException(
					$"JsonPit tests require Os.TempDir ('{baseRoot.Path}') to point to a writable root that allows creating directories and writing files. " +
					"Please update Os.TempDir configuration before running these tests.",
					ex);
			}
		}
	}
}
