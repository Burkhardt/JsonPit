using System;
using System.IO;
using OsLib;

namespace JsonPit.Tests
{
	internal static class JsonPitTestEnvironment
	{
		private static readonly string cloudRoot;

		static JsonPitTestEnvironment()
		{
			cloudRoot = ((new RaiPath(Path.GetTempPath())) / "JsonPitTests" / "CloudRoot" / Guid.NewGuid().ToString("N")).Path;
			Directory.CreateDirectory(cloudRoot);

			Environment.SetEnvironmentVariable("OSLIB_CLOUD_ROOT_GOOGLEDRIVE", cloudRoot);
			Environment.SetEnvironmentVariable("OSLIB_CLOUD_ROOT_DROPBOX", null);
			Environment.SetEnvironmentVariable("OSLIB_CLOUD_ROOT_ONEDRIVE", null);
			Environment.SetEnvironmentVariable("OSLIB_CLOUD_ROOT_ICLOUD", null);
			Environment.SetEnvironmentVariable("OSLIB_CLOUD_CONFIG", null);

			Os.ResetCloudStorageCache();
		}

		internal static string CloudRoot => cloudRoot;

		internal static string CloudPath(params string[] segments)
		{
			var path = new RaiPath(CloudRoot);
			foreach (var segment in segments)
				path = path / segment;
			return path.Path;
		}

		internal static string CloudFile(params string[] segments)
		{
			if (segments == null || segments.Length == 0)
				throw new ArgumentException("At least one segment is required.", nameof(segments));

			var path = new RaiPath(CloudRoot);
			for (var i = 0; i < segments.Length - 1; i++)
				path = path / segments[i];

			return new RaiFile(path.Path + segments[^1]).FullName;
		}
	}
}