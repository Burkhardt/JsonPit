using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

public sealed class ReceiptMaintenanceTests : IDisposable
{
	private readonly RaiPath root = Os.TempDir / "RAIkeep" / "jsonpit-tests" / $"receipts-{Guid.NewGuid():N}" / "Tenant";
	private readonly TimeSpan originalGrace = Pit.ChangeFileCleanupGrace;

	public ReceiptMaintenanceTests()
	{
		root.mkdir();
		new TextFile((root.Parent / "JsonPit").mkdir(), "timing", "json5", "{SyncPitDurationSeconds:0,SyncFlagDurationSeconds:0,BufferSeconds:0}");
	}

	[Fact]
	public void Save_HistoricalSnapshotPublishesFreshLeaseAndNotifiesEarlierReaders()
	{
		var directory = (root / "OldSnapshot").mkdir();
		using var pit = new Pit(directory, readOnly: false, autoload: false);
		var historical = DateTimeOffset.UtcNow.AddDays(-1);
		pit.AddHistorical(new PitItem("Past", true, historical));
		var beforeWrite = DateTimeOffset.UtcNow.AddSeconds(-2);
		pit.Save(force: true);

		Assert.Equal("Returned", pit.MasterFlag().ReadTicket().State);
		Assert.Equal(historical, pit["Past"].Modified);
		Assert.False(pit.MasterUpdatesAvailable());
		// A checkpoint newer than the historical data still needs this publication.
		Assert.True(pit.MasterFlag().ReadTicket().Written > beforeWrite);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void OrdinaryWritableOpen_PreservesBacklogUntilMaintenance(bool openFromPitFile)
	{
		var directory = (root / "Backlog").mkdir();
		var changes = new System.Collections.Generic.List<RaiFile>();
		var modified = DateTimeOffset.UtcNow.AddDays(-2);
		using (var fixture = new Pit(directory, readOnly: false, unflagged: true, autoload: false))
		{
			for (int i = 0; i < 102; i++)
				fixture.AddHistorical(new PitItem($"Item{i}", true, modified.AddSeconds(i)));
			fixture.Save(force: true);
			for (int i = 0; i < 102; i++)
			{
				var fragment = new PitItem($"Item{i}", true, modified.AddSeconds(i));
				var file = fixture.CreateChangeFile(fragment, i % 2 == 0 ? fixture.ExactProcessIdentity : "Peer-app-42");
				changes.Add(file);
				if (i < 101)
				{
					var age = i < 100 ? TimeSpan.FromMinutes(11) : TimeSpan.Zero;
					var receipt = new TextFile(ReceiptFile.PathFor(file.FullName).FullName)
					{
						Lines = [(DateTimeOffset.UtcNow - age).ToString("o")], Changed = true
					};
					receipt.Save();
				}
			}
		}
		var canonical = new PitFile(directory, name: "Backlog");
		var lastWrite = canonical.LastWriteTimeUtc;
		using (var reader = new Pit(directory, readOnly: true, unflagged: true))
			Assert.All(changes, file => Assert.True(file.Exists()));
		using var opened = openFromPitFile ? new Pit(canonical, readOnly: false) : new Pit(directory, readOnly: false);
		Assert.Equal(lastWrite, canonical.LastWriteTimeUtc);
		Assert.Equal(100, opened.Maintain(apply: true).ChangeFilesRemoved);
		Assert.All(changes.Take(100), file =>
		{
			Assert.False(file.Exists());
			Assert.False(ReceiptFile.PathFor(file.FullName).Exists());
		});
		Assert.All(changes.Skip(100), file =>
		{
			Assert.True(file.Exists());
			Assert.True(ReceiptFile.PathFor(file.FullName).Exists());
		});
		Assert.Equal(102, opened.HistoricItems.Count);
	}

	public void Dispose()
	{
		TicketFixture.SetCleanupGrace(originalGrace);
		try { root.Parent.rmdir(depth: 6, deleteFiles: true); } catch { }
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Maintain_MissingPitDirectory_PerformsNoFilesystemMutation(bool apply)
	{
		var parent = (root / $"missing-parent-{apply}").mkdir();
		var missing = parent / "Activity";
		var parentWriteTime = parent.LastWriteTimeUtc;
		var before = parent.EnumerateFiles("*", SearchOption.AllDirectories)
			.Select(file => file.FullName)
			.Concat(parent.EnumerateDirectories("*", SearchOption.AllDirectories).Select(path => path.FullPath))
			.OrderBy(path => path, StringComparer.Ordinal)
			.ToArray();

		using var pit = new Pit(missing, readOnly: !apply, unflagged: true, autoload: false);
		var result = pit.Maintain(apply);

		var after = parent.EnumerateFiles("*", SearchOption.AllDirectories)
			.Select(file => file.FullName)
			.Concat(parent.EnumerateDirectories("*", SearchOption.AllDirectories).Select(path => path.FullPath))
			.OrderBy(path => path, StringComparer.Ordinal)
			.ToArray();
		Assert.False(missing.Exists());
		Assert.Equal(before, after);
		Assert.Equal(parentWriteTime, parent.LastWriteTimeUtc);
		Assert.Contains(result.Deferred, message => message.Contains("does not exist", StringComparison.Ordinal));
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
		TicketFixture.SetCleanupGrace(TimeSpan.FromHours(1));

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

		TicketFixture.SetCleanupGrace(TimeSpan.Zero);
		var cleanup = reopened.Maintain(apply: true);

		Assert.Equal(1, cleanup.ChangeFilesRemoved);
		Assert.False(change.Exists());
		Assert.False(ReceiptFile.PathFor(change.FullName).Exists());
		Assert.NotNull(reopened["Peer"]);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Maintain_HistoricalChanges_RevalidatesExactOwnerAfterStore(bool foreignTakeover)
	{
		var directory = (root / "Historical").mkdir();
		using var pit = new Pit(directory, readOnly: false, autoload: false);
		var old = DateTimeOffset.UtcNow.AddDays(-8);
		var change = pit.CreateChangeFile(new PitItem("First", true, old), "Peer-app-123");
		Assert.True(pit.Maintain(apply: true).CanonicalPersisted);
		Assert.Equal("Returned", pit.MasterFlag().ReadTicket().State);
		Assert.False(pit.MasterUpdatesAvailable());
		var receipt = new TextFile(ReceiptFile.PathFor(change.FullName).FullName)
		{
			Lines = [DateTimeOffset.UtcNow.AddMinutes(-11).ToString("o")], Changed = true
		};
		receipt.Save();
		pit.CreateChangeFile(new PitItem("Second", true, old.AddMinutes(5)), "Peer-app-123");
		if (foreignTakeover)
			pit.RecoveryStatusChanged += status =>
			{
				if (status.Stage == RecoveryStage.CleanupPending)
					TicketFixture.WriteMaster(new MasterFlagFile(directory, "Master"), originator: "OtherHost-peer-42");
			};

		var result = pit.Maintain(apply: true);

		Assert.True(result.CanonicalPersisted);
		Assert.Equal(1, result.ChangeFilesEligible);
		Assert.Equal(foreignTakeover ? 0 : 1, result.ChangeFilesRemoved);
		Assert.Equal(result.ChangeFilesRemoved, result.ReceiptsRemoved);
		Assert.Equal(foreignTakeover, change.Exists());
		Assert.Equal(foreignTakeover, receipt.Exists());
		if (foreignTakeover)
		{
			Assert.Contains($"Cleanup revalidation deferred {change.NameWithExtension}", result.Deferred);
			Assert.True(pit.MasterFlag().IsOwnedBy("OtherHost-peer-42"));
		}
		else
		{
			Assert.Empty(result.Deferred);
			Assert.Equal("Returned", pit.MasterFlag().ReadTicket().State);
		}
		var canonical = new TextFile(pit.JsonFile.FullName).ReadAllText();
		Assert.Contains("\"First\"", canonical);
		Assert.Contains("\"Second\"", canonical);
	}

	[Fact]
	public void Local_ReceiptSurvivesRestart_AndGraceExpiryRemovesBothArtifacts()
	{
		var localRoot = RAIkeepTestEnvironment.LocalPath(
			"cr021-receipts", $"receipt-{Guid.NewGuid():N}");
		try
		{
			localRoot.mkdir();
			RaiFile change;
			DateTimeOffset receiptTime;
			TicketFixture.SetCleanupGrace(TimeSpan.FromHours(1));

			using (var first = new Pit(localRoot, readOnly: false, unflagged: true, autoload: false))
			{
				first.Add(new PitItem("CloudBase"));
				first.Save(force: true);
				var fragment = new PitItem("CloudPeer");
				fragment.SetProperty(new { Value = "persisted through local storage" });
				change = first.CreateChangeFile(fragment, "RemotePeer-cloud-4242");
				var canonicalized = first.Maintain(apply: true);
				Assert.Equal(1, canonicalized.ReceiptsCreated);
				receiptTime = new ReceiptFile(change.FullName).Time;
			}

			using var reopened = new Pit(localRoot, readOnly: false, unflagged: true, autoload: true);
			Assert.Equal(receiptTime, new ReceiptFile(change.FullName).Time);
			Assert.NotNull(reopened["CloudPeer"]);

			TicketFixture.SetCleanupGrace(TimeSpan.Zero);
			var cleanup = reopened.Maintain(apply: true);

			Assert.Equal(1, cleanup.ChangeFilesRemoved);
			Assert.False(change.Exists());
			Assert.False(ReceiptFile.PathFor(change.FullName).Exists());
			Assert.NotNull(reopened["CloudPeer"]);
		}
		finally
		{
			ConfiguredCloudPits.Cleanup(localRoot);
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
		TicketFixture.SetCleanupGrace(TimeSpan.Zero);

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
		TicketFixture.SetCleanupGrace(TimeSpan.FromHours(1));

		var retained = pit.Maintain(apply: true);

		Assert.True(receipt.Exists());
		Assert.Equal(1, retained.ReceiptsOrphaned);

		TicketFixture.SetCleanupGrace(TimeSpan.Zero);
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
		var active = new TextFile(pitPath, $"{Environment.MachineName}-pits-{Environment.ProcessId}", "flag")
		{
			Lines = [new TimestampedValue($"{Environment.MachineName}:pits:{Environment.ProcessId}", DateTimeOffset.UtcNow).ToString()],
			Changed = true
		};
		active.Save();
		var master = new MasterFlagFile(pitPath, "Master");
		TicketFixture.WriteMaster(master, originator: "Nkosikazi-pits-99999");

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
		// Construct actual old bytes/names: today's EventFile deliberately emits no hash.
		var (canonical, sha) = CanonicalJson.CanonicalizeWithHash(content);
		var stem = $"639245160038875300_Nkosikazi-AIA.Api-93455_{sha}";
		var eventPath = (pitPath / EventDirectory.Name).mkdir();
		var typed = new RaiFile(eventPath, stem, EventFile.Extension);
		var legacy = new RaiFile(eventPath, stem);
		new TextFile(legacy.FullName).WriteAllText(canonical);
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
