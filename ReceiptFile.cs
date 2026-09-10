using System;
using System.Globalization;
using OsLib;

namespace JsonPit;

/// <summary>
/// Immutable cleanup-eligibility receipt for one JsonPit change file (CR021).
/// The receipt is stored beside the change file with the same logical name and
/// the <c>.receipt</c> extension. Its complete content is the UTC time at which
/// canonical accounting first succeeded.
/// </summary>
public sealed class ReceiptFile : TextFile
{
	public const string Extension = "receipt";

	/// <summary>The associated change file's complete path.</summary>
	public string ChangeFileFullName { get; }

	/// <summary>The original, immutable receipt time.</summary>
	public DateTimeOffset Time { get; }

	/// <summary>
	/// Opens the existing receipt for <paramref name="changeFileFullName"/>, or
	/// creates it once with the current UTC time when absent. An existing receipt
	/// is never rewritten or refreshed.
	/// </summary>
	public ReceiptFile(string changeFileFullName)
		: this(RequireChangeFile(changeFileFullName))
	{
	}

	private ReceiptFile(RaiFile changeFile)
		: base(changeFile.Path, changeFile.Name, Extension)
	{
		// Preserve the complete hash-bearing change stem even when a consumer is
		// temporarily built against an older OsLibCore package whose TextFile
		// constructor interpreted dots in explicit logical names as extensions.
		NameAndExt = (changeFile.Name, Extension);
		ChangeFileFullName = changeFile.FullName;
		if (Exists())
		{
			var persisted = Read();
			if (persisted.Count != 1 ||
				!DateTimeOffset.TryParseExact(
					persisted[0],
					"o",
					CultureInfo.InvariantCulture,
					DateTimeStyles.RoundtripKind,
					out var time))
				throw new FormatException($"Receipt file '{FullName}' must contain exactly one round-trip timestamp.");
			Time = time.ToUniversalTime();
			return;
		}

		Time = DateTimeOffset.UtcNow;
		Lines = [Time.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)];
		Changed = true;
		Save();
	}

	/// <summary>Returns the deterministic receipt path without creating it.</summary>
	public static RaiFile PathFor(string changeFileFullName)
	{
		var changeFile = RequireChangeFile(changeFileFullName);
		return new RaiFile(changeFile.Path, changeFile.Name, Extension);
	}

	private static RaiFile RequireChangeFile(string changeFileFullName)
	{
		if (string.IsNullOrWhiteSpace(changeFileFullName))
			throw new ArgumentException("A change-file full name is required.", nameof(changeFileFullName));
		var changeFile = new RaiFile(changeFileFullName);
		if (!string.Equals(changeFile.Ext, "json", StringComparison.OrdinalIgnoreCase) ||
			!ChangeFile.TryParseName(changeFile.Name, out _, out _, out _))
			throw new ArgumentException(
				$"'{changeFileFullName}' is not a hashed JsonPit change-file name.",
				nameof(changeFileFullName));
		return changeFile;
	}
}
