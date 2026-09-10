using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using OsLib;

namespace JsonPit;

/// <summary>Explicit, cloud-safe compaction of loose JsonPit recovery events.</summary>
internal static class PitEventArchiver
{
	private const string MetadataEntryName = "Events.metadata.json";

	internal static void Archive(RaiPath pitDirectory, PitMaintenanceResult result, bool apply)
		=> Archive(pitDirectory, result, apply, snapshotCaptured: null);

	internal static void Archive(
		RaiPath pitDirectory,
		PitMaintenanceResult result,
		bool apply,
		Action snapshotCaptured)
	{
		var eventsPath = pitDirectory / EventDirectory.Name;
		if (!eventsPath.Exists()) return;

		var snapshot = EventDirectory.Inspect(pitDirectory);
		result.EventFilesObserved = eventsPath.EnumerateFiles($"*.{EventFile.Extension}").Count();
		result.EventFilesValid = snapshot.LooseEvents.Count;
		result.EventFilesInvalid = result.EventFilesObserved - result.EventFilesValid;
		result.EventArchivesObserved = snapshot.Archives.Count;
		foreach (var issue in snapshot.Issues)
			result.Deferred.Add(issue);

		var loose = snapshot.LooseEvents
			.ToDictionary(
				pair => pair.Key,
				pair => new LooseEvent(
					new RaiFile(eventsPath, pair.Key),
					RaiZipEntry.FromFile(new RaiFile(eventsPath, pair.Key)),
					new PitAuditEvent(pair.Key, pair.Value)),
				StringComparer.Ordinal);
		snapshotCaptured?.Invoke();

		var alreadyArchived = FindAlreadyArchived(snapshot.Archives, loose, result);
		result.EventFilesAlreadyArchived = alreadyArchived.Count;
		if (apply)
			RetireLooseEvents(alreadyArchived.Select(name => loose[name]), result);

		var selected = loose.Values
			.Where(item => !alreadyArchived.Contains(item.File.NameWithExtension))
			.Where(item => IsTimestampUsable(item, result))
			.OrderBy(item => item.File.NameWithExtension, StringComparer.Ordinal)
			.ToList();
		result.EventFilesEligible = selected.Count;
		if (selected.Count == 0) return;

		var start = selected.Min(item => item.Event.UtcTime).ToUniversalTime();
		var end = selected.Max(item => item.Event.UtcTime).ToUniversalTime();
		var archiveStem = $"Events_{FormatRangeTime(start)}_to_{FormatRangeTime(end)}";
		var archive = new RaiZipFile(eventsPath, archiveStem);
		result.EventArchiveName = archive.NameWithExtension;
		result.EventArchiveStartUtc = start;
		result.EventArchiveEndUtc = end;

		if (!apply) return;

		var metadata = CanonicalJson.Canonicalize(new JObject
		{
			["EventCount"] = selected.Count,
			["StartUtc"] = start.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
			["EndUtc"] = end.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
			["TimeBasis"] = "UTC"
		});
		var entries = selected.Select(item => item.Entry)
			.Append(RaiZipEntry.FromText(MetadataEntryName, metadata))
			.ToList();

		RaiZipWriteResult write;
		try
		{
			write = archive.CreateImmutable(entries);
		}
		catch (Exception exception)
		{
			result.Failures.Add($"Event archive could not be created at '{archive.FullName}': {exception.Message}");
			return;
		}

		switch (write.Status)
		{
			case RaiZipWriteStatus.Created:
				result.EventArchivesCreated++;
				break;
			case RaiZipWriteStatus.ExistingIdentical:
				result.EventArchivesReused++;
				break;
			case RaiZipWriteStatus.ExistingDifferent:
			case RaiZipWriteStatus.ExistingInvalid:
				result.Deferred.Add(
					$"Event archive pathname collision retained without replacement: {archive.NameWithExtension}: {write.Problem}");
				return;
		}

		result.EventFilesArchived = selected.Count;
		RetireLooseEvents(selected, result);
	}

	private static HashSet<string> FindAlreadyArchived(
		IReadOnlyList<RaiZipFile> archives,
		IReadOnlyDictionary<string, LooseEvent> loose,
		PitMaintenanceResult result)
	{
		var archived = new HashSet<string>(StringComparer.Ordinal);
		foreach (var archive in archives)
		{
			if (!archive.TryReadEntries(out var entries, out _)) continue;
			foreach (var pair in loose)
			{
				if (entries.TryGetValue(pair.Key, out var existing) &&
					existing.Content.SequenceEqual(pair.Value.Entry.Content))
					archived.Add(pair.Key);
			}
		}
		return archived;
	}

	private static bool IsTimestampUsable(LooseEvent item, PitMaintenanceResult result)
	{
		if (item.Event.UtcTime != DateTimeOffset.MinValue) return true;
		result.Deferred.Add($"Event has no valid UTC timestamp and was not archived: {item.File.NameWithExtension}");
		return false;
	}

	private static void RetireLooseEvents(IEnumerable<LooseEvent> events, PitMaintenanceResult result)
	{
		foreach (var item in events)
		{
			try
			{
				if (!item.File.Exists()) continue;
				var current = RaiZipEntry.FromFile(item.File);
				if (!current.Content.SequenceEqual(item.Entry.Content))
				{
					result.Deferred.Add($"Loose event changed after the archive snapshot and was retained: {item.File.NameWithExtension}");
					continue;
				}
				item.File.rm();
				if (item.File.Exists())
					result.Failures.Add($"Archived loose event remained after removal: {item.File.FullName}");
				else
					result.EventFilesRemoved++;
			}
			catch (Exception exception)
			{
				result.Failures.Add($"Archived loose event could not be removed: {item.File.FullName}: {exception.Message}");
			}
		}
	}

	private static string FormatRangeTime(DateTimeOffset value)
		=> value.UtcDateTime.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

	private sealed record LooseEvent(RaiFile File, RaiZipEntry Entry, PitAuditEvent Event);
}
