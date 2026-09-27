using System;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using OsLib;

namespace JsonPit;

/// <summary>
/// Clean change-file identity and validated payload access (CR041).
/// <para>
/// New ordinary change files use <c>{Modified.UtcTicks}_{ExactProcessIdentity}.json</c>.
/// Existing CR003 names with a trailing SHA-256 remain readable during rolling upgrades.
/// </para>
/// <para>
/// The exact identity is the process-flag stem containing machine, subscriber/application,
/// and PID. Strict JSON parsing defers incomplete cloud materializations.
/// </para>
/// </summary>
public static class ChangeFile
{
	private static readonly Regex CleanName = new(
		@"^(?<ticks>[0-9]{18})_(?<identity>[A-Za-z0-9.\-]+-[A-Za-z0-9.\-]+-[0-9]+)$",
		RegexOptions.CultureInvariant | RegexOptions.Compiled);
	private static readonly Regex LegacyHashedName = new(
		@"^(?<ticks>[0-9]{18})_(?<identity>[A-Za-z0-9.\-]+-[A-Za-z0-9.\-]+-[0-9]+)_(?<sha>[0-9a-f]{64})$",
		RegexOptions.CultureInvariant | RegexOptions.Compiled);

	/// <summary>
	/// Canonicalizes the persisted change-file payload for one fragment:
	/// an array of history arrays containing exactly this fragment.
	/// </summary>
	public static (string CanonicalPayload, string Sha256) CanonicalPayloadFor(PitItem fragment)
	{
		if (fragment is null) throw new ArgumentNullException(nameof(fragment));
		var payload = new JArray(new JArray(fragment.DeepClone()));
		return CanonicalJson.CanonicalizeWithHash(payload);
	}

	/// <summary>Composes the change-file name (without extension) for one fragment.</summary>
	public static string ComposeName(PitItem fragment, string exactProcessIdentity)
	{
		if (fragment is null) throw new ArgumentNullException(nameof(fragment));
		return ComposeName(fragment.Modified, exactProcessIdentity);
	}

	/// <summary>Composes the clean CR041 name without an extension.</summary>
	public static string ComposeName(DateTimeOffset modified, string exactProcessIdentity)
	{
		var name = $"{modified.UtcTicks}_{exactProcessIdentity}";
		if (!CleanName.IsMatch(name))
			throw new ArgumentException(
				$"Exact process identity '{exactProcessIdentity}' cannot be represented in a JsonPit change-file name.",
				nameof(exactProcessIdentity));
		return name;
	}

	/// <summary>Composes the legacy CR003 hashed name for compatibility fixtures.</summary>
	public static string ComposeName(DateTimeOffset modified, string exactProcessIdentity, string sha256) =>
		$"{modified.UtcTicks}_{exactProcessIdentity}_{sha256}";

	/// <summary>
	/// Parses either the clean CR041 name or the legacy CR003 hashed name.
	/// <paramref name="sha256"/> is null for a clean name.
	/// </summary>
	public static bool TryParseName(string nameWithoutExtension, out long utcTicks, out string exactProcessIdentity, out string sha256)
	{
		utcTicks = 0;
		exactProcessIdentity = null;
		sha256 = null;
		if (string.IsNullOrEmpty(nameWithoutExtension)) return false;
		var match = LegacyHashedName.Match(nameWithoutExtension);
		if (!match.Success) match = CleanName.Match(nameWithoutExtension);
		if (!match.Success || !long.TryParse(match.Groups["ticks"].Value, out utcTicks)) return false;
		exactProcessIdentity = match.Groups["identity"].Value;
		sha256 = match.Groups["sha"].Success ? match.Groups["sha"].Value : null;
		return true;
	}

	/// <summary>
	/// Parses the identity segment of either a clean CR041 or legacy hashed change-file
	/// name. Returns null when the name does not look like a change file at all.
	/// </summary>
	public static string IdentityOf(string nameWithoutExtension)
	{
		if (TryParseName(nameWithoutExtension, out _, out var identity, out _))
			return identity;
		return null;
	}

	/// <summary>
	/// Reads a materialized change file and validates it for merge: for legacy hashed
	/// names the exact file content — written as canonical UTF-8 JSON with no trailing line
	/// terminator — must match the hash encoded in the filename, and the canonical payload
	/// must parse completely. Clean CR041 names are validated by strict JSON parsing.
	/// </summary>
	/// <returns>The parsed payload, or null when the file is not yet mergeable.</returns>
	public static JArray ReadValidated(RaiFile file)
	{
		if (file is null) throw new ArgumentNullException(nameof(file));
		string content;
		try { content = new TextFile(file.FullName).ReadAllText(); }
		catch (IOException) { return null; }
		if (!TryParseName(file.Name, out _, out _, out var sha256)) return null;
		if (!string.IsNullOrEmpty(sha256) && CanonicalJson.Sha256Hex(content) != sha256)
			return null; // incomplete or corrupted legacy materialization — not mergeable yet
		if (string.IsNullOrWhiteSpace(content)) return null;
		try
		{
			return JArray.Parse(content);
		}
		catch (Newtonsoft.Json.JsonException)
		{
			return null;
		}
	}
}
