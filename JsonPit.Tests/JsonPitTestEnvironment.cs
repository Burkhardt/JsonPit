using System;
using System.IO;
using Newtonsoft.Json.Linq;
using OsLib;

namespace JsonPit.Tests
{
	internal static class JsonPitTestEnvironment
	{
		private static readonly string cloudRoot;

		static JsonPitTestEnvironment()
		{
			var testRoot = (new RaiPath(Path.GetTempPath())) / "JsonPitTests" / Guid.NewGuid().ToString("N");
			var home = (testRoot / "home").Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			var appData = (testRoot / "app-data").Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			var localAppData = (testRoot / "local-app-data").Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			cloudRoot = (testRoot / "CloudRoot").Path;

			Directory.CreateDirectory(home);
			Directory.CreateDirectory(appData);
			Directory.CreateDirectory(localAppData);
			Directory.CreateDirectory(cloudRoot);

			Environment.SetEnvironmentVariable("HOME", home);
			Environment.SetEnvironmentVariable("USERPROFILE", home);
			Environment.SetEnvironmentVariable("APPDATA", appData);
			Environment.SetEnvironmentVariable("LOCALAPPDATA", localAppData);
			Environment.SetEnvironmentVariable("HOMEDRIVE", null);
			Environment.SetEnvironmentVariable("HOMEPATH", null);

			var configPath = Os.GetDefaultConfigPath();
			var configFile = new RaiFile(configPath);
			RaiFile.mkdir(configFile.Path);
			File.WriteAllText(configFile.FullName, new JObject
			{
				["cloud"] = new JObject
				{
					["dropbox"] = string.Empty,
					["onedrive"] = string.Empty,
					["googledrive"] = new RaiPath(cloudRoot).Path,
					["icloud"] = string.Empty
				}
			}.ToString());

			Os.LoadConfig(refresh: true);
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