using System;
using OsLib;
namespace JsonPit.Tests
{
	internal static class RAIkeepTestEnvironment
	{
		private static readonly Lazy<RaiPath> rootPath = new(ResolveWritableRootPath);
		internal static RaiPath RootPath => rootPath.Value;
		internal static RaiPath LocalPath(params string[] segments)
		{
			var path = RootPath;
			foreach (var segment in segments)
				path = path / segment;
			return path;
		}
		internal static string LocalFile(params string[] segments)
		{
			if (segments == null || segments.Length == 0)
				throw new ArgumentException("At least one segment is required.", nameof(segments));
			var path = RootPath;
			for (var i = 0; i < segments.Length - 1; i++)
				path = path / segments[i];
			return new RaiFile(path, segments[^1]).FullName;
		}
		private static RaiPath ResolveWritableRootPath()
		{
			var candidate = Os.TempDir / ("RAIkeep.Tests-" + Guid.NewGuid().ToString("N"));
			if (candidate.Cloud) throw new InvalidOperationException("Local tests require a non-cloud temp directory.");
			candidate.mkdir();
			AppDomain.CurrentDomain.ProcessExit += (_, _) => ConfiguredCloudPits.Cleanup(candidate);
			return candidate;
		}
	}
}
