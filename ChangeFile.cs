using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using OsLib;

namespace JsonPit;

/// <summary>
/// Collision-safe change-file identity and validated payload access
/// (CR003, coordinated v3.13.2).
/// <para>
/// Every ordinary change file — non-master persistence, split-master recovery, and
/// graceful-shutdown export — uses <c>{Modified.UtcTicks}_{ExactProcessIdentity}_{Sha256}.json</c>.
/// The exact identity is the process-flag stem containing machine, subscriber/application,
/// and PID. <c>Sha256</c> is the full lowercase SHA-256 of the exact canonical UTF-8 JSON
/// payload written to the file.
/// </para>
/// <para>
/// The timestamp remains diagnostic and sortable but is not treated as unique. Distinct
/// equal-timestamp fragments therefore cannot suppress one another, while repeating the
/// same fragment produces the same filename and stays idempotent.
/// </para>
/// </summary>
public static class ChangeFile
{
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
		var (_, sha) = CanonicalPayloadFor(fragment);
		return ComposeName(fragment.Modified, exactProcessIdentity, sha);
	}

	public static string ComposeName(DateTimeOffset modified, string exactProcessIdentity, string sha256) =>
		$"{modified.UtcTicks}_{exactProcessIdentity}_{sha256}";

	/// <summary>
	/// Parses a change-file name (without extension). Returns false for the legacy
	/// pre-3.13.2 form <c>{ticks}_{identity}</c> without a trailing content hash.
	/// </summary>
	public static bool TryParseName(string nameWithoutExtension, out long utcTicks, out string exactProcessIdentity, out string sha256)
	{
		utcTicks = 0;
		exactProcessIdentity = null;
		sha256 = null;
		if (string.IsNullOrEmpty(nameWithoutExtension)) return false;
		var firstSeparator = nameWithoutExtension.IndexOf('_');
		var lastSeparator = nameWithoutExtension.LastIndexOf('_');
		if (firstSeparator <= 0 || lastSeparator <= firstSeparator) return false;
		if (!long.TryParse(nameWithoutExtension[..firstSeparator], out utcTicks)) return false;
		var hashCandidate = nameWithoutExtension[(lastSeparator + 1)..];
		if (hashCandidate.Length != 64 || !hashCandidate.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
			return false;
		exactProcessIdentity = nameWithoutExtension[(firstSeparator + 1)..lastSeparator];
		sha256 = hashCandidate;
		return exactProcessIdentity.Length > 0;
	}

	/// <summary>
	/// Parses the identity segment of either a hashed (v3.13.2) or legacy change-file
	/// name. Returns null when the name does not look like a change file at all.
	/// </summary>
	public static string IdentityOf(string nameWithoutExtension)
	{
		if (TryParseName(nameWithoutExtension, out _, out var identity, out _))
			return identity;
		// Legacy form: {ticks}_{identity}
		var separator = nameWithoutExtension?.IndexOf('_') ?? -1;
		if (separator <= 0 || separator == nameWithoutExtension.Length - 1) return null;
		return long.TryParse(nameWithoutExtension[..separator], out _)
			? nameWithoutExtension[(separator + 1)..]
			: null;
	}

	/// <summary>
	/// Reads a materialized change file and validates it for merge: for hashed (v3.13.2)
	/// names the exact file content — written as canonical UTF-8 JSON with no trailing line
	/// terminator — must match the hash encoded in the filename, and the canonical payload
	/// must parse completely. Legacy names without a hash are still parse-validated for
	/// upgrade compatibility but are never credited with the hash guarantee.
	/// </summary>
	/// <returns>The parsed payload, or null when the file is not yet mergeable.</returns>
	public static JArray ReadValidated(RaiFile file)
	{
		if (file is null) throw new ArgumentNullException(nameof(file));
		string content;
		try { content = File.ReadAllText(file.FullName, new UTF8Encoding(false)); }
		catch (IOException) { return null; }
		if (TryParseName(file.Name, out _, out _, out var sha256))
		{
			if (CanonicalJson.Sha256Hex(content) != sha256)
				return null; // incomplete or corrupted materialization — not mergeable yet
		}
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
