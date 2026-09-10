using System;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

public sealed class EventArchiveMaintenanceTests : IDisposable
{
	private readonly RaiPath root = Os.TempDir / "RAIkeep" / "jsonpit-tests" / "event-archive";
	private readonly RaiPath pitDirectory;

	public EventArchiveMaintenanceTests()
	{
		Cleanup();
		pitDirectory = (root / "Activity").mkdir();
	}

	public void Dispose() => Cleanup();

	[Fact]
	public void Maintain_ArchiveEventsPreviewReportsExactRangeWithoutMutation()
	{
		var first = WriteEvent(new DateTimeOffset(2026, 8, 4, 1, 18, 12, TimeSpan.Zero), "first");
		var second = WriteEvent(new DateTimeOffset(2026, 9, 10, 16, 43, 59, TimeSpan.Zero), "second");
		using var pit = OpenPit();

		var result = pit.Maintain(new PitMaintenanceOptions { ArchiveEvents = true });

		Assert.False(result.Applied);
		Assert.Equal(2, result.EventFilesObserved);
		Assert.Equal(2, result.EventFilesEligible);
		Assert.Equal("Events_20260804-0118_to_20260910-1643.zip", result.EventArchiveName);
		Assert.Equal(new DateTimeOffset(2026, 8, 4, 1, 18, 12, TimeSpan.Zero), result.EventArchiveStartUtc);
		Assert.Equal(new DateTimeOffset(2026, 9, 10, 16, 43, 59, TimeSpan.Zero), result.EventArchiveEndUtc);
		Assert.True(first.Exists());
		Assert.True(second.Exists());
		Assert.Empty((pitDirectory / EventDirectory.Name).EnumerateFiles("*.zip"));
	}

	[Fact]
	public void Maintain_ArchiveEventsApplyCreatesSameDirectoryArchive_RemovesLoose_AndAuditReadsArchive()
	{
		WriteEvent(new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero), "first");
		WriteEvent(new DateTimeOffset(2026, 9, 10, 12, 1, 0, TimeSpan.Zero), "second");
		using var pit = OpenPit();

		var result = pit.Maintain(new PitMaintenanceOptions { Apply = true, ArchiveEvents = true });

		Assert.Equal(1, result.EventArchivesCreated);
		Assert.Equal(2, result.EventFilesArchived);
		Assert.Equal(2, result.EventFilesRemoved);
		var eventsPath = pitDirectory / EventDirectory.Name;
		Assert.Empty(eventsPath.EnumerateFiles("*.event"));
		var archive = Assert.Single(eventsPath.EnumerateFiles("*.zip"));
		Assert.Equal(result.EventArchiveName, archive.NameWithExtension);
		Assert.StartsWith(eventsPath.FullPath, archive.FullName, StringComparison.Ordinal);
		Assert.Equal(eventsPath.FullPath, archive.Path.FullPath);

		var audit = PitAudit.Inspect(pitDirectory);
		Assert.Equal(2, audit.Events.Count);
		Assert.Empty(audit.Issues);
		var zip = new RaiZipFile(archive.FullName);
		Assert.True(zip.TryReadEntries(out var entries, out var problem), problem);
		Assert.Contains("Events.metadata.json", entries.Keys);
		var metadata = System.Text.Encoding.UTF8.GetString(entries["Events.metadata.json"].Content);
		Assert.Contains("\"TimeBasis\":\"UTC\"", metadata, StringComparison.Ordinal);
	}

	[Fact]
	public void Maintain_ArchiveEventsRetryRetiresLooseCopyAlreadyPresentInImmutableArchive()
	{
		var time = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
		var content = EventContent(time, "retry");
		var logicalStem = $"{time.UtcTicks}_TestMachine-tests-1_Completed";
		var original = new EventFile(pitDirectory, logicalStem, content);
		using var pit = OpenPit();
		var first = pit.Maintain(new PitMaintenanceOptions { Apply = true, ArchiveEvents = true });
		Assert.Equal(1, first.EventArchivesCreated);
		var firstArchive = new RaiZipFile(Assert.Single((pitDirectory / EventDirectory.Name).EnumerateFiles("*.zip")).FullName);
		Assert.True(firstArchive.TryReadEntries(out var firstEntries, out var firstProblem), firstProblem);
		Assert.Contains(original.NameWithExtension, firstEntries.Keys);

		var replay = new EventFile(pitDirectory, logicalStem, (JObject)content.DeepClone());
		Assert.Equal(original.NameWithExtension, replay.NameWithExtension);
		Assert.True(replay.Exists());
		var retry = pit.Maintain(new PitMaintenanceOptions { Apply = true, ArchiveEvents = true });

		Assert.True(
			retry.EventFilesAlreadyArchived == 1,
			$"archives={retry.EventArchivesObserved}; valid={retry.EventFilesValid}; deferred={string.Join(" | ", retry.Deferred)}");
		Assert.Equal(1, retry.EventFilesRemoved);
		Assert.Equal(0, retry.EventArchivesCreated);
		Assert.False(replay.Exists());
		Assert.Single((pitDirectory / EventDirectory.Name).EnumerateFiles("*.zip"));
		Assert.Single(PitAudit.Read(pitDirectory));
	}

	[Fact]
	public void Maintain_ArchiveEventsCollisionPreservesExistingArchiveAndLooseEvidence()
	{
		var loose = WriteEvent(new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero), "collision");
		var eventsPath = pitDirectory / EventDirectory.Name;
		var archive = new RaiZipFile(eventsPath, "Events_20260910-1200_to_20260910-1200");
		Assert.Equal(
			RaiZipWriteStatus.Created,
			archive.CreateImmutable([RaiZipEntry.FromText("foreign.event", "foreign")]).Status);
		var archiveWriteTime = archive.LastWriteTimeUtc;
		using var pit = OpenPit();

		var result = pit.Maintain(new PitMaintenanceOptions { Apply = true, ArchiveEvents = true });

		Assert.True(loose.Exists());
		Assert.Equal(archiveWriteTime, archive.LastWriteTimeUtc);
		Assert.Equal(0, result.EventFilesRemoved);
		Assert.Contains(result.Deferred, message =>
			message.Contains("collision retained without replacement", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void Maintain_ArchiveEventsCorruptSourcesAndArchiveFailSafeWithoutEvidenceLoss()
	{
		var loose = WriteEvent(new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero), "valid");
		var eventsPath = pitDirectory / EventDirectory.Name;
		var invalidLoose = new TextFile(eventsPath, "not-a-valid-event-hash", EventFile.Extension)
		{
			Lines = ["{\"Message\":\"broken\"}"],
			Changed = true
		};
		invalidLoose.Save();
		var corruptArchive = new TextFile(eventsPath, "Events_20260910-1200_to_20260910-1200", "zip")
		{
			Lines = ["not a zip archive"],
			Changed = true
		};
		corruptArchive.Save();
		var corruptContent = corruptArchive.ReadAllText();
		using var pit = OpenPit();

		var result = pit.Maintain(new PitMaintenanceOptions { Apply = true, ArchiveEvents = true });

		Assert.Equal(2, result.EventFilesObserved);
		Assert.Equal(1, result.EventFilesValid);
		Assert.Equal(1, result.EventFilesInvalid);
		Assert.Equal(0, result.EventFilesRemoved);
		Assert.True(loose.Exists());
		Assert.True(invalidLoose.Exists());
		Assert.Equal(corruptContent, corruptArchive.ReadAllText());
		Assert.Contains(result.Deferred, message => message.Contains("Invalid loose event", StringComparison.Ordinal));
		Assert.Contains(result.Deferred, message => message.Contains("Invalid event archive", StringComparison.Ordinal));
		Assert.Contains(result.Deferred, message => message.Contains("collision retained", StringComparison.Ordinal));
	}

	[Fact]
	public void Audit_DeduplicatesLooseAndArchivedCopiesByEventIdentity_AndReportsConflict()
	{
		var time = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
		var eventId = Guid.NewGuid();
		var originalContent = EventContent(time, "same", eventId);
		var archivedSource = new EventFile(
			pitDirectory,
			$"{time.UtcTicks}_Archived-tests-1_Completed",
			originalContent);
		var archivedEntry = RaiZipEntry.FromFile(archivedSource);
		var archive = new RaiZipFile(pitDirectory / EventDirectory.Name, "Events_20260910-1200_to_20260910-1200");
		Assert.Equal(
			RaiZipWriteStatus.Created,
			archive.CreateImmutable([
				archivedEntry,
				RaiZipEntry.FromText("Events.metadata.json", "{\"TimeBasis\":\"UTC\"}")
			]).Status);
		archivedSource.rm();
		_ = new EventFile(
			pitDirectory,
			$"{time.UtcTicks}_Loose-tests-1_Completed",
			(JObject)originalContent.DeepClone());

		var deduplicated = PitAudit.Inspect(pitDirectory);

		Assert.Single(deduplicated.Events);
		Assert.Empty(deduplicated.Issues);

		var conflictingContent = (JObject)originalContent.DeepClone();
		conflictingContent["Message"] = "different";
		_ = new EventFile(
			pitDirectory,
			$"{time.UtcTicks}_Conflicting-tests-1_Completed",
			conflictingContent);
		var conflicted = PitAudit.Inspect(pitDirectory);

		Assert.Single(conflicted.Events);
		Assert.Contains(conflicted.Issues, issue =>
			issue.Contains($"EventId '{eventId:D}'", StringComparison.Ordinal));
	}

	[Fact]
	public void Archive_LeavesEventPublishedAfterStableSnapshotLooseForNextBatch()
	{
		var snapshotted = WriteEvent(
			new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero),
			"snapshotted");
		EventFile concurrent = null;
		var result = new PitMaintenanceResult("Activity.pit", applied: true);

		PitEventArchiver.Archive(
			pitDirectory,
			result,
			apply: true,
			snapshotCaptured: () => concurrent = WriteEvent(
				new DateTimeOffset(2026, 9, 10, 12, 1, 0, TimeSpan.Zero),
				"published after snapshot"));

		Assert.False(snapshotted.Exists());
		Assert.NotNull(concurrent);
		Assert.True(concurrent.Exists());
		Assert.Equal(1, result.EventFilesArchived);
		Assert.Equal(1, result.EventFilesRemoved);
		var audit = PitAudit.Read(pitDirectory);
		Assert.Equal(2, audit.Count);
		Assert.Contains(audit, item => item.Message == "snapshotted");
		Assert.Contains(audit, item => item.Message == "published after snapshot");
	}

	private Pit OpenPit() => new(
		pitDirectory,
		readOnly: true,
		undercover: true,
		unflagged: true,
		autoload: false);

	private EventFile WriteEvent(DateTimeOffset time, string message)
		=> new(
			pitDirectory,
			$"{time.UtcTicks}_TestMachine-tests-1_Completed",
			EventContent(time, message));

	private static JObject EventContent(DateTimeOffset time, string message, Guid? eventId = null)
	{
		var status = new RecoveryStatus(
			RecoveryStatus.CurrentSchemaVersion,
			eventId ?? Guid.NewGuid(),
			time,
			LogLevel.Information,
			RecoveryStage.Completed,
			"Activity",
			"TestMachine",
			"TestMachine-tests-1",
			string.Empty,
			RecoveryRole.Master,
			0,
			0,
			Guid.NewGuid(),
			"Test",
			message,
			string.Empty);
		return status.ToJObject();
	}

	private void Cleanup()
	{
		try
		{
			if (root.Exists()) root.rmdir(depth: 10, deleteFiles: true);
		}
		catch { }
	}
}
