using System;
using System.Linq;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

/// <summary>
/// Resolves TestCloud for local concurrency suites through the dynamic <see cref="Os.Config"/> object
/// (<c>Os.Config?.Cloud?[provider]</c>). The machine configuration file is the sole
/// source of truth: no environment variables, rewritten configuration, dependency
/// injection, or local temporary substitutes. A missing prerequisite produces an
/// explicit skip with a precise reason; a skip is never release-acceptance evidence.
/// </summary>
internal static class ConfiguredCloudPits
{
	internal static string Provider => Os.TestCloud;
	internal static readonly string RunDirectory = $"RAIkeep.Test.{Guid.NewGuid():N}";

	/// <summary>Configured TestCloud only: local tests never fall back to a live provider.</summary>
	internal static string ProviderNameOrNull()
	{
		if (Provider is null) return null;
		string root = Os.CloudRoot(Provider).FullPath;
		return !string.IsNullOrWhiteSpace(root) && new RaiPath(root).Exists() ? Provider : null;
	}

	internal static RaiPath RequirePitRoot(params string[] segments)
	{
		if (ProviderNameOrNull() is null)
			Assert.Skip("Local cloud tests require an available Os.TestCloud root; no OneDrive fallback.");
		var path = Os.CloudRoot(Provider) / RunDirectory;
		foreach (var segment in segments)
			path /= segment;
		return path;
	}

	internal static RaiPath CreateTimedPitRoot(params string[] segments)
	{
		var directory = RequirePitRoot(segments);
		try
		{
			_ = PitTiming.Load(directory);
			return directory.mkdir();
		}
		catch (UnauthorizedAccessException ex)
		{
			Assert.Skip("Configured TestCloud inaccessible: " + ex.Message);
			throw;
		}
	}

	internal static string PeerTestRoot(string host, string testId)
	{
		var result = SshSystem.ExecuteRemoteCommand(host, "cat ~/.config/RAIkeep.json5", timeoutMilliseconds: 5000);
		if (!result.Succeeded) Assert.Skip($"Cannot read {host}'s TestCloud configuration.");
		var config = PitJson.ParseObject(result.StandardOutput);
		if ((string)config["TestCloud"] != Provider) Assert.Skip($"The TestCloud roles on this host and {host} differ.");
		// Resolve the remote mapping with Amafu on the peer, including its native home and links.
		var lookup = new AmafuCommand().OverSsh(host).Run(["list", "cloud", Provider], 5000);
		var path = lookup.StandardOutput.Trim().TrimEnd('/');
		if (!lookup.Succeeded || !path.StartsWith('/') ||
			!System.Text.RegularExpressions.Regex.IsMatch(path, @"^/[\w ./-]+$") || path.Split('/').Contains(".."))
			Assert.Skip($"Cannot use the configured TestCloud path on {host} for the SSH fixture.");
		return $"{path}/{RunDirectory}/{testId}";
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
