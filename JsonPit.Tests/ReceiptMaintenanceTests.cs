using System;
using System.Globalization;
using System.Threading;
using Newtonsoft.Json.Linq;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

public sealed class ReceiptMaintenanceTests : IDisposable
{
	private readonly RaiPath root = Os.TempDir / "RAIkeep" / "jsonpit-tests" / $"receipts-{Guid.NewGuid():N}";
	private readonly TimeSpan originalGrace = Pit.ChangeFileCleanupGrace;

	public ReceiptMaintenanceTests() => root.mkdir();

	public void Dispose()
	{
		Pit.ChangeFileCleanupGrace = originalGrace;
		try { root.rmdir(depth: 6, deleteFiles: true); } catch { }
	}

	[Fact]
	public void ReceiptFile_UsesChangeStemAndOneImmutableUtcTimestamp()
	{
		var change = ValidChangePath(root, "Nkosikazi-AIA.Api-93455");

		var first = new ReceiptFile(change.FullName);
		var originalTime = first.Time;
		var originalWriteTime = first.LastWriteTimeUtc;
		var originalContent = first.ReadAllText();
		Thread.Sleep(25);
		var reopened = new ReceiptFile(change.FullName);

		Assert.Equal(change.Name, first.Name);
		Assert.Equal(ReceiptFile.Extension, first.Ext);
		Assert.Equal(change.Path.FullPath, first.Path.FullPath);
		Assert.Equal(originalTime, reopened.Time);
		Assert.Equal(originalWriteTime, reopened.LastWriteTimeUtc);
		Assert.Equal(originalContent, reopened.ReadAllText());
		Assert.True(DateTimeOffset.TryParseExact(
			originalContent.Trim(), "o", CultureInfo.InvariantCulture,
			DateTimeStyles.RoundtripKind, out _));
	}

	[Fact]
	public void ReceiptTime_SurvivesPitRestart_AndLaterPassRemovesChangeThenReceipt()
	{
		var pitPath = (root / "Activity").mkdir();
		RaiFile change;
		DateTimeOffset receiptTime;
		DateTimeOffset receiptWriteTime;
		Pit.ChangeFileCleanupGrace = TimeSpan.FromHours(1);

		using (var first = new Pit(pitPath, readOnly: false, unflagged: true, autoload: false))
		{
			first.Add(new PitItem("Base"));
			first.Save(force: true);
			var fragment = new PitItem("Peer");
			fragment.SetProperty(new { Value = 42 });
			change = first.CreateChangeFile(fragment, "RemotePeer-app-4242");
			var pass = first.Maintain(apply: true);
			Assert.Equal(1, pass.ReceiptsCreated);
			var receipt = new ReceiptFile(change.FullName);
			receiptTime = receipt.Time;
			receiptWriteTime = receipt.LastWriteTimeUtc;
			Assert.True(change.Exists());
		}

		using var reopened = new Pit(pitPath, readOnly: false, unflagged: true, autoload: true);
		var preserved = new ReceiptFile(change.FullName);
		Assert.Equal(receiptTime, preserved.Time);
		Assert.Equal(receiptWriteTime, preserved.LastWriteTimeUtc);
		Assert.NotNull(reopened["Peer"]);

		Pit.ChangeFileCleanupGrace = TimeSpan.Zero;
		var cleanup = reopened.Maintain(apply: true);

		Assert.Equal(1, cleanup.ChangeFilesRemoved);
		Assert.False(change.Exists());
		Assert.False(ReceiptFile.PathFor(change.FullName).Exists());
		Assert.NotNull(reopened["Peer"]);
	}

	[Fact]
	public void ConfiguredCloud_ReceiptSurvivesRestart_AndGraceExpiryRemovesBothArtifacts()
	{
		var cloudRoot = ConfiguredCloudPits.RequirePitRoot(
			"cr021-receipts", $"receipt-{Guid.NewGuid():N}");
		try
		{
			cloudRoot.mkdir();
			RaiFile change;
			DateTimeOffset receiptTime;
			Pit.ChangeFileCleanupGrace = TimeSpan.FromHours(1);

			using (var first = new Pit(cloudRoot, readOnly: false, unflagged: true, autoload: false))
			{
				first.Add(new PitItem("CloudBase"));
				first.Save(force: true);
				var fragment = new PitItem("CloudPeer");
				fragment.SetProperty(new { Value = "persisted through configured cloud storage" });
				change = first.CreateChangeFile(fragment, "RemotePeer-cloud-4242");
				var canonicalized = first.Maintain(apply: true);
				Assert.Equal(1, canonicalized.ReceiptsCreated);
				receiptTime = new ReceiptFile(change.FullName).Time;
			}

			using var reopened = new Pit(cloudRoot, readOnly: false, unflagged: true, autoload: true);
			Assert.Equal(receiptTime, new ReceiptFile(change.FullName).Time);
			Assert.NotNull(reopened["CloudPeer"]);

			Pit.ChangeFileCleanupGrace = TimeSpan.Zero;
			var cleanup = reopened.Maintain(apply: true);

			Assert.Equal(1, cleanup.ChangeFilesRemoved);
			Assert.False(change.Exists());
			Assert.False(ReceiptFile.PathFor(change.FullName).Exists());
			Assert.NotNull(reopened["CloudPeer"]);
		}
		finally
		{
			ConfiguredCloudPits.Cleanup(cloudRoot);
		}
	}

	[Fact]
	public void MalformedReceipt_NeverAuthorizesChangeDeletion()
	{
		var pitPath = (root / "Object").mkdir();
		using var pit = new Pit(pitPath, readOnly: false, unflagged: true, autoload: false);
		pit.Add(new PitItem("Base"));
		pit.Save(force: true);
		var change = pit.CreateChangeFile(new PitItem("Peer"), "RemotePeer-app-4242");
		pit.Maintain(apply: true);
		var receiptPath = ReceiptFile.PathFor(change.FullName);
		var malformed = new TextFile(receiptPath.FullName)
		{
			Lines = ["not-a-timestamp"],
			Changed = true
		};
		malformed.Save();
		Pit.ChangeFileCleanupGrace = TimeSpan.Zero;

		var result = pit.Maintain(apply: true);

		Assert.True(change.Exists());
		Assert.True(receiptPath.Exists());
		Assert.True(result.ReceiptsMalformed > 0);
		Assert.Equal(0, result.ChangeFilesRemoved);
	}

	[Fact]
	public void OrphanReceipt_IsRemovedOnlyAfterItsGrace()
	{
		var pitPath = (root / "Place").mkdir();
		using var pit = new Pit(pitPath, readOnly: false, unflagged: true, autoload: false);
		pit.Add(new PitItem("Base"));
		pit.Save(force: true);
		var missingChange = ValidChangePath(pitPath, "RemotePeer-app-4242");
		var receipt = new ReceiptFile(missingChange.FullName);
		Pit.ChangeFileCleanupGrace = TimeSpan.FromHours(1);

		var retained = pit.Maintain(apply: true);

		Assert.True(receipt.Exists());
		Assert.Equal(1, retained.ReceiptsOrphaned);

		Pit.ChangeFileCleanupGrace = TimeSpan.Zero;
		var removed = pit.Maintain(apply: true);

		Assert.False(receipt.Exists());
		Assert.Equal(1, removed.ReceiptsRemoved);
	}

	[Fact]
	public void ProcessFlagPruning_RequiresExplicitApplyAndAge_AndPreservesAuthorityFiles()
	{
		var pitPath = (root / "Flags").mkdir();
		using var pit = new Pit(pitPath, readOnly: false, unflagged: true, autoload: false);
		var expired = new TextFile(pitPath, "Nkosikazi-pits-12345", "flag")
		{
			Lines = [new TimestampedValue("Nkosikazi:pits:12345", DateTimeOffset.UtcNow.AddDays(-10)).ToString()],
			Changed = true
		};
		expired.Save();
		var active = new TextFile(pitPath, "Nkosikazi-pits-67890", "flag")
		{
			Lines = [new TimestampedValue("Nkosikazi:pits:67890", DateTimeOffset.UtcNow).ToString()],
			Changed = true
		};
		active.Save();
		var master = new MasterFlagFile(pitPath, "Master");
		master.Update(originator: "Nkosikazi-pits-99999");

		var report = pit.Maintain();
		Assert.True(expired.Exists());
		Assert.Equal(1, report.ProcessFlagsExpired);
		Assert.Equal(1, report.ProcessFlagsActive);

		var applied = pit.Maintain(new PitMaintenanceOptions
		{
			Apply = true,
			PruneProcessFlags = true,
			OlderThan = TimeSpan.FromDays(7)
		});

		Assert.False(expired.Exists());
		Assert.True(active.Exists());
		Assert.True(master.Exists());
		Assert.Equal(1, applied.ProcessFlagsPruned);
	}

	[Fact]
	public void LegacyDottedProcessFlag_IsInventoriedThenRepairedOnlyWhenAuthorized()
	{
		var pitPath = (root / "Legacy").mkdir();
		using var pit = new Pit(pitPath, readOnly: false, unflagged: true, autoload: false);
		const string legacyStem = "Nkosikazi-AIA.Api-93455";
		var legacy = new TextFile(pitPath, legacyStem)
		{
			Lines = [new TimestampedValue("Nkosikazi:AIA.Api:93455", DateTimeOffset.UtcNow.AddDays(-1)).ToString()],
			Changed = true
		};
		legacy.Save();
		var repaired = new RaiFile(pitPath, legacyStem, "flag");

		var report = pit.Maintain();
		Assert.Equal(1, report.LegacyArtifactsObserved);
		Assert.True(legacy.Exists());
		Assert.False(repaired.Exists());

		var apply = pit.Maintain(new PitMaintenanceOptions
		{
			Apply = true,
			RepairLegacyExtensions = true
		});

		Assert.Equal(1, apply.LegacyArtifactsRepaired);
		Assert.False(legacy.Exists());
		Assert.True(repaired.Exists());
		Assert.Equal("Nkosikazi:AIA.Api:93455", new TimestampedValue(new TextFile(repaired.FullName).Read()[0]).Value);
	}

	[Fact]
	public void LegacyDottedRecoveryEvent_IsInventoriedThenRepairedOnlyWhenAuthorized()
	{
		var pitPath = (root / "LegacyEvent").mkdir();
		using var pit = new Pit(pitPath, readOnly: false, unflagged: true, autoload: false);
		var content = new RecoveryStatus(
			RecoveryStatus.CurrentSchemaVersion,
			Guid.NewGuid(),
			DateTimeOffset.UtcNow,
			Microsoft.Extensions.Logging.LogLevel.Information,
			RecoveryStage.Completed,
			"Activity",
			"Nkosikazi",
			"AIA.Api",
			string.Empty,
			RecoveryRole.Master,
			0,
			0,
			Guid.NewGuid(),
			"Fixture",
			"Legacy dotted event fixture",
			string.Empty).ToJObject();
		var typed = new EventFile(pitPath, "639245160038875300_Nkosikazi-AIA.Api-93455", content);
		var legacy = new RaiFile(typed.Path, typed.Name);
		legacy.mv(typed);
		Assert.True(legacy.Exists());
		Assert.False(typed.Exists());
		var legacyContent = new TextFile(legacy.FullName).ReadAllText();
		var expectedHash = legacy.NameWithExtension[(legacy.NameWithExtension.LastIndexOf('_') + 1)..];
		Assert.Equal(expectedHash, CanonicalJson.Sha256Hex(legacyContent));
		Assert.Single((pitPath / EventDirectory.Name).EnumerateFiles("*"));

		var report = pit.Maintain();
		Assert.Equal(1, report.LegacyArtifactsObserved);
		Assert.True(legacy.Exists());

		var apply = pit.Maintain(new PitMaintenanceOptions
		{
			Apply = true,
			RepairLegacyExtensions = true
		});

		Assert.Equal(1, apply.LegacyArtifactsRepaired);
		Assert.False(legacy.Exists());
		Assert.True(typed.Exists());
		Assert.Single(PitAudit.Read(pitPath));
	}

	[Fact]
	public void DestructiveMaintenanceOptions_AreRejectedUnlessExplicitlyApplied()
	{
		using var pit = new Pit((root / "Validation").mkdir(), readOnly: false, unflagged: true, autoload: false);
		Assert.Throws<ArgumentException>(() => pit.Maintain(new PitMaintenanceOptions
		{
			PruneProcessFlags = true,
			OlderThan = TimeSpan.FromDays(1)
		}));
		Assert.Throws<ArgumentException>(() => pit.Maintain(new PitMaintenanceOptions
		{
			Apply = true,
			OlderThan = TimeSpan.FromDays(1)
		}));
		Assert.Throws<ArgumentException>(() => pit.Maintain(new PitMaintenanceOptions
		{
			RepairLegacyExtensions = true
		}));
	}

	private static RaiFile ValidChangePath(RaiPath directory, string identity)
	{
		var fragment = new PitItem("ReceiptFixture");
		var (_, sha) = ChangeFile.CanonicalPayloadFor(fragment);
		return new RaiFile(directory, ChangeFile.ComposeName(fragment.Modified, identity, sha), "json");
	}
}
