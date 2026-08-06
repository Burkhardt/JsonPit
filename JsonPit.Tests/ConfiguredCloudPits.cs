using System;
using System.Collections.Generic;
using System.IO;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

/// <summary>
/// Resolves the real configured cloud roots for the CR003 concurrency suites exactly
/// like the pits CLI does — through the dynamic <see cref="Os.Config"/> object
/// (<c>Os.Config?.Cloud?[provider]</c>). The machine configuration file is the sole
/// source of truth: no environment variables, rewritten configuration, dependency
/// injection, or local temporary substitutes. A missing prerequisite produces an
/// explicit skip with a precise reason; a skip is never release-acceptance evidence.
/// </summary>
internal static class ConfiguredCloudPits
{
	private static readonly string[] FallbackOrder = { "OneDrive", "Dropbox", "GoogleDrive", "ICloudDrive" };

	private static IEnumerable<string> ProviderOrder()
	{
		var order = new List<string>();
		try
		{
			var configured = Os.Config?.DefaultCloudOrder;
			if (configured is not null)
				foreach (var provider in configured)
					order.Add((string)provider);
		}
		catch { }
		return order.Count > 0 ? order : FallbackOrder;
	}

	/// <summary>First configured cloud provider whose root exists on this machine, or null.</summary>
	internal static string ProviderNameOrNull()
	{
		foreach (var provider in ProviderOrder())
		{
			string root = (string)Os.Config?.Cloud?[provider];
			if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(new RaiPath(root).Path))
				return provider;
		}
		return null;
	}

	/// <summary>
	/// A fresh pit directory under the first configured cloud root, or an explicit skip
	/// when the machine has no configured provider.
	/// </summary>
	internal static RaiPath RequirePitRoot(params string[] segments)
	{
		var provider = ProviderNameOrNull();
		if (provider is null)
			Assert.Skip("No configured cloud provider root (Os.Config.Cloud) is available on this machine; " +
				"CR003 concurrency tests require a real configured cloud root and must not use a local substitute.");
		var path = new RaiPath((string)Os.Config.Cloud[provider]) / "RAIkeep" / "jsonpit-cr003-tests";
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
