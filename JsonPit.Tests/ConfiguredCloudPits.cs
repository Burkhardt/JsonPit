using System;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

/// <summary>
/// Resolves Dropbox for local concurrency suites through the dynamic <see cref="Os.Config"/> object
/// (<c>Os.Config?.Cloud?[provider]</c>). The machine configuration file is the sole
/// source of truth: no environment variables, rewritten configuration, dependency
/// injection, or local temporary substitutes. A missing prerequisite produces an
/// explicit skip with a precise reason; a skip is never release-acceptance evidence.
/// </summary>
internal static class ConfiguredCloudPits
{
	internal const string Provider = "Dropbox";
	internal static readonly string RunDirectory = $"RAIkeep.Test.{Guid.NewGuid():N}";

	/// <summary>Dropbox only: local tests never fall back to a live provider.</summary>
	internal static string ProviderNameOrNull()
	{
		string root = (string)Os.Config?.Cloud?[Provider];
		return !string.IsNullOrWhiteSpace(root) && new RaiPath(root).Exists() ? Provider : null;
	}

	internal static RaiPath RequirePitRoot(params string[] segments)
	{
		if (ProviderNameOrNull() is null)
			Assert.Skip("Local cloud tests require an available Os.Config.Cloud.Dropbox root; no OneDrive fallback.");
		var path = new RaiPath((string)Os.Config.Cloud[Provider]) / RunDirectory;
		foreach (var segment in segments)
			path /= segment;
		return path;
	}

	internal static void Cleanup(RaiPath root)
	{
		try
		{
			if (root?.Exists() == true)
				root.rmdir(depth: 10, deleteFiles: true);
		}
		catch { }
	}
}
