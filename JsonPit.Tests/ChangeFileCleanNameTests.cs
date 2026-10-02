using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

public sealed class ChangeFileCleanNameTests : IDisposable
{
	private readonly RaiPath root = Os.TempDir / "RAIkeep" / "jsonpit-tests" / $"cr041-{Guid.NewGuid():N}";

	public ChangeFileCleanNameTests() => root.mkdir();
	public void Dispose()
	{
		try { root.rmdir(depth: 5, deleteFiles: true); } catch { }
	}

	[Fact]
	public void ComposeName_Emits4CharChecksum_AndTryParseAcceptsAllFormats()
	{
		var timestamp = DateTimeOffset.Parse("2026-09-26T20:00:00Z");
		const string identity = "Nkosikazi-AIA.Api-41360";
		const string hash4 = "a3f7";

		// 4-character checksum format
		var hashed4 = ChangeFile.ComposeName(timestamp, identity, hash4);
		Assert.Matches(new Regex(@"^[0-9]{18}_[A-Za-z0-9.\-]+-[A-Za-z0-9.\-]+-[0-9]+_[0-9a-f]{4}$"), hashed4);
		Assert.True(ChangeFile.TryParseName(hashed4, out var ticks4, out var id4, out var parsedHash4));
		Assert.Equal(timestamp.UtcTicks, ticks4);
		Assert.Equal(identity, id4);
		Assert.Equal(hash4, parsedHash4);

		// Clean unhashed format (CR041)
		var clean = ChangeFile.ComposeName(timestamp, identity);
		Assert.Matches(new Regex(@"^[0-9]{18}_[A-Za-z0-9.\-]+-[A-Za-z0-9.\-]+-[0-9]+$"), clean);
		Assert.True(ChangeFile.TryParseName(clean, out var cleanTicks, out var cleanIdentity, out var cleanHash));
		Assert.Equal(timestamp.UtcTicks, cleanTicks);
		Assert.Equal(identity, cleanIdentity);
		Assert.Null(cleanHash);

		// Legacy 64-char hash format (CR003)
		var legacyHash = new string('a', 64);
		var legacy = ChangeFile.ComposeName(timestamp, identity, legacyHash);
		Assert.True(ChangeFile.TryParseName(legacy, out var legacyTicks, out var legacyIdentity, out var parsedHash));
		Assert.Equal(timestamp.UtcTicks, legacyTicks);
		Assert.Equal(identity, legacyIdentity);
		Assert.Equal(legacyHash, parsedHash);
	}

	[Fact]
	public void ChangeFile_With4CharChecksum_IsPrefixValidated_AndTruncationRejected()
	{
		var fragment = new PitItem("Sipho");
		fragment.SetProperty(new { Role = "Musician" });
		var (payload, sha) = ChangeFile.CanonicalPayloadFor(fragment);
		var changeName = ChangeFile.ComposeName(fragment, "Nkosikazi-pits-41360");
		Assert.EndsWith($"_{sha[..4]}", changeName);

		var change = new RaiFile(root, changeName, "json");
		File.WriteAllText(change.FullName, payload, new UTF8Encoding(false));

		// Valid payload passes prefix check
		Assert.NotNull(ChangeFile.ReadValidated(change));
		var receipt = new ReceiptFile(change.FullName);
		Assert.Equal(change.Name, receipt.Name);
		Assert.Equal("receipt", receipt.Ext);

		// Mismatched / truncated payload fails prefix check
		File.WriteAllText(change.FullName, "[]", new UTF8Encoding(false));
		Assert.Null(ChangeFile.ReadValidated(change));
	}

	[Fact]
	public void LegacyHashedChange_StillChecksHash()
	{
		var fragment = new PitItem("Sipho");
		var (payload, hash) = ChangeFile.CanonicalPayloadFor(fragment);
		var valid = new RaiFile(root, ChangeFile.ComposeName(fragment.Modified, "Nkosikazi-pits-41360", hash), "json");
		File.WriteAllText(valid.FullName, payload, new UTF8Encoding(false));
		Assert.NotNull(ChangeFile.ReadValidated(valid));

		File.WriteAllText(valid.FullName, payload + " ", new UTF8Encoding(false));
		Assert.Null(ChangeFile.ReadValidated(valid));
	}
}
