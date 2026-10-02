using System;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using OsLib;

namespace JsonPit;

/// <summary>
/// Clean change-file identity and validated payload access (CR041).
/// <para>
	/// New ordinary change files use <c>{Modified.UtcTicks}_{ExactProcessIdentity}_{sha256-prefix4}.json</c>.
	/// Clean CR041 names and CR003 names with a full SHA-256 remain readable during rolling upgrades.
/// </para>
/// <para>
/// The exact identity is the process-flag stem containing machine, subscriber/application,
/// and PID. Strict JSON parsing defers incomplete cloud materializations.
/// </para>
/// </summary>
public static class ChangeFile
{
	private static readonly Regex HashedName = new(
		@"^(?<ticks>[0-9]{18})_(?<identity>[A-Za-z0-9.\-]+-[A-Za-z0-9.\-]+-[0-9]+)_(?<sha>[0-9a-f]{4}|[0-9a-f]{64})$",
		RegexOptions.CultureInvariant | RegexOptions.Compiled);
	private static readonly Regex CleanName = new(
		@"^(?<ticks>[0-9]{18})_(?<identity>[A-Za-z0-9.\-]+-[A-Za-z0-9.\-]+-[0-9]+)$",
		RegexOptions.CultureInvariant | RegexOptions.Compiled);

	/// <summary>
	/// Canonicalizes the persisted change-file payload for one fragment:
	/// a JSON array containing exactly this fragment.
	/// </summary>
	public static (string CanonicalPayload, string Sha256) CanonicalPayloadFor(PitItem fragment)
	{
		if (fragment is null) throw new ArgumentNullException(nameof(fragment));
		var payload = new JArray(new JArray(fragment.DeepClone()));
		return CanonicalJson.CanonicalizeWithHash(payload);
	}

	/// <summary>Composes the change-file name (without extension) for one fragment with a 4-character checksum.</summary>
	public static string ComposeName(PitItem fragment, string exactProcessIdentity)
	{
		if (fragment is null) throw new ArgumentNullException(nameof(fragment));
		var (_, sha) = CanonicalPayloadFor(fragment);
		return ComposeName(fragment.Modified, exactProcessIdentity, sha[..4]);
	}

	/// <summary>Composes the clean change-file name with a 4-character checksum (or legacy 64-character hash).</summary>
	public static string ComposeName(DateTimeOffset modified, string exactProcessIdentity, string hash)
	{
		var name = $"{modified.UtcTicks}_{exactProcessIdentity}_{hash}";
		if (!HashedName.IsMatch(name))
			throw new ArgumentException(
				$"Exact process identity '{exactProcessIdentity}' or hash '{hash}' cannot be represented in a JsonPit change-file name.",
				nameof(exactProcessIdentity));
		return name;
	}

	/// <summary>Composes the clean CR041 unhashed name without an extension.</summary>
	public static string ComposeName(DateTimeOffset modified, string exactProcessIdentity)
	{
		var name = $"{modified.UtcTicks}_{exactProcessIdentity}";
		if (!CleanName.IsMatch(name))
			throw new ArgumentException(
				$"Exact process identity '{exactProcessIdentity}' cannot be represented in a JsonPit change-file name.",
				nameof(exactProcessIdentity));
		return name;
	}

	/// <summary>
	/// Parses the 4-char hashed, clean CR041, or legacy CR003 hashed name.
	/// <paramref name="sha"/> is null for an unhashed clean name.
	/// </summary>
	public static bool TryParseName(string nameWithoutExtension, out long utcTicks, out string exactProcessIdentity, out string sha)
	{
		utcTicks = 0;
		exactProcessIdentity = null;
		sha = null;
		if (string.IsNullOrEmpty(nameWithoutExtension)) return false;
		var match = HashedName.Match(nameWithoutExtension);
		if (!match.Success) match = CleanName.Match(nameWithoutExtension);
		if (!match.Success || !long.TryParse(match.Groups["ticks"].Value, out utcTicks)) return false;
		exactProcessIdentity = match.Groups["identity"].Value;
		sha = match.Groups["sha"].Success ? match.Groups["sha"].Value : null;
		return true;
	}

	/// <summary>
	/// Parses the identity segment of a change-file name.
	/// Returns null when the name does not look like a change file at all.
	/// </summary>
	public static string IdentityOf(string nameWithoutExtension)
	{
		if (TryParseName(nameWithoutExtension, out _, out var identity, out _))
			return identity;
		return null;
	}

	/// <summary>
	/// Reads a materialized change file and validates it for merge: if a checksum is encoded
	/// in the filename (4-char or legacy 64-char), the content's SHA-256 must match that prefix.
	/// Clean unhashed CR041 names are validated by strict JSON parsing.
	/// </summary>
	/// <returns>The parsed payload, or null when the file is not yet mergeable.</returns>
	public static JArray ReadValidated(RaiFile file)
	{
		if (file is null) throw new ArgumentNullException(nameof(file));
		string content;
		try { content = new TextFile(file.FullName).ReadAllText(); }
		catch (IOException) { return null; }
		if (!TryParseName(file.Name, out _, out _, out var sha)) return null;
		if (!string.IsNullOrEmpty(sha))
		{
			var actualSha = CanonicalJson.Sha256Hex(content);
			if (!actualSha.StartsWith(sha, StringComparison.OrdinalIgnoreCase))
				return null; // incomplete or corrupted materialization — not mergeable yet
		}
		if (string.IsNullOrWhiteSpace(content)) return null;
		try
		{
			return PitJson.ParseArray(content);
		}
		catch (Newtonsoft.Json.JsonException)
		{
			return null;
		}
	}
}
