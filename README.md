# JsonPit

JsonPit change requests and release notes are centralized in the RAIkeep [`doc/`](https://github.com/Burkhardt/RAIkeep/tree/main/doc) directory under `JsonPit_...` filenames; they are not stored separately in this child repository.

	Stores JsonPits, json files with a value history, across machines/servers ("eventually persistent").

## Start Here

If you want to use JsonPit 4.2.2 from NuGet in another service or agent workflow, start with [GettingStarted.md](https://github.com/Burkhardt/JsonPit/blob/main/GettingStarted.md).

That guide now covers:

- package setup for `JsonPit`, `RaiUtils`, and `OsLibCore`
- current `Pit` and `PitItem` usage patterns
- the recommended long-lived in-memory server usage pattern
- querying and enumeration patterns that are actually supported
- persistence and synchronized-storage expectations
- a practical `PersonPit` example for OTW / AfricaStage style backend work

## 4.2.2

- Aligns on `OsLibCore 4.2.2` and `RaiUtils 4.2.2`.
- Makes `UseLocalRAIkeepSources=false` an explicit package-only boundary so release validation cannot silently resolve sibling projects.
- `FileSystemWatcher` callbacks and debounce work now use weak ownership so an abandoned `Pit` can be collected and its canonical path reopened.
- The `Pit` finalizer still performs no recovery publication, watcher disposal, or filesystem I/O.
- The remote synchronization suite now waits for authority-record contents rather than only a materialized `Master.flag` pathname.
- Keeps the WWWA-based quick-start section in [GettingStarted.md](https://github.com/Burkhardt/JsonPit/blob/main/GettingStarted.md) for cloud-path pit creation and sample JSON5 seeding.
- The supported cloud-backed provider claim is `Dropbox`, `OneDrive`, `GoogleDrive`, and `ICloudDrive`.
- `PitItem.Id` is now the canonical framework identifier.
- Legacy payloads that still contain `Name` without `Id` are normalized internally by copying `Name` into `Id`, while preserving `Name`.
- Future use of `Name` as an application-defined custom field remains supported.
- `PitItem.DeleteProperty(...)` now projects top-level null tombstones as absent attributes instead of leaking a permanent null shadow.
- Remote-sync workflows continue to align with OsLib's configurable metadata propagation delay handling, including the `mkdir` polymorphism package line update in OsLib.
- No JsonPit API changes were required beyond the `3.12.0` line; this release refreshes the aligned package baseline and packaged docs.
- Live docs and release-note pointers were refreshed for the `4.2.2` release line, and this README is packaged with the NuGet release.

## namespace

JsonPit

## classes

### ItemsBase: Base container holding a key identifier for item groups.

- ItemsBase: `Key`

### JsonPitBase: Common base for pits with config, flags, and persistence helpers.

- JsonPitBase: `ReadOnly`, `Backup`, `RunningOnMaster`, `MasterUpdatesAvailable`, `TryReleaseProcessWindow`, `ChangeDir`, `JsonFile`

### TimestampedValue: Value with an attached timestamp and round-trip string format.

- TimestampedValue: `Value`, `Time`, `ToString`

### MasterFlagFile: Flag file used to track master ownership and last update time.

- MasterFlagFile: `Originator`, `Time`, `Update`

### ProcessFlagFile: Flag file used to track the current process and last update time.

- ProcessFlagFile: `Process`, `Update`, `CurrentProcessId`, `CurrentFlagName`, `IsOwnedByCurrentProcess`, `TryReleaseCurrentProcess`
- Activity filename: `{MachineName}-{Subscriber}-{PID}.flag`; the PID makes ownership process-specific.
- Explicit release verifies the current process identity and expires the flag in place without deleting the cloud-synced file.
- Process activity windows and master writer tickets are separate; releasing the former never releases the latter.

### PitItem: JSON-backed item with metadata and change tracking.

- PitItem: `Id`, `Modified`, `Deleted`, `Note`, `SetProperty`, `DeleteProperty`

### PitItemExtensions: Helpers for comparing items and aligning timestamps.

- PitItemExtensions: `Equals`, `isLike`, `aligned`

### PitItems: History stack of PitItem versions for a single key.

- PitItems: `Push`, `Peek`, `Get`, `Merge`, `Count`

### Pit: JsonPit file container with item history and persistence.

- Pit: `Add`, `Get`, `GetAt`, `Delete`, `Save`, `MergeChanges`, `Keys`

## cloud root convention

JsonPit resolves cloud-backed storage locations through OsLib, but the current approach is to read an explicit configured root from `Os.Config.Cloud` rather than relying on a preferred-root helper.

For Ubuntu development machines, especially when Google Drive is mounted through `rclone`, GNOME integration, or a team-specific mount path, prefer explicit configuration instead of probe-only discovery.

Recommended shared contract:
- Use `RAIkeep.json5` to point the supported provider roots `Cloud.Dropbox`, `Cloud.OneDrive`, `Cloud.GoogleDrive`, and `Cloud.ICloudDrive` at the active synchronized mounts.
- Keep that file at `~/.config/RAIkeep.json5`.
- Reuse the same PascalCase keys as OsLib.

That keeps JsonPit aligned with OsLib in .NET today and with the upcoming Python `OsLib`, `RaiUtils`, and `JsonPit` packages later.

### Item: Base item with modified tracking and dirty state management.

- Item: `Id`, `Modified`, `Deleted`, `Delete`, `Valid`, `Validate`, `Invalidate`
---

@see [GettingStarted.md](https://github.com/Burkhardt/JsonPit/blob/main/GettingStarted.md) for the practical onboarding guide, or check the unit tests for lower-level API examples.

Foldable class and contract documentation is available in
[API.md](https://github.com/Burkhardt/JsonPit/blob/main/API.md).

## release notes

- Latest release notes: [JsonPit_RELEASE_NOTES_4.2.2.md](https://github.com/Burkhardt/RAIkeep/blob/main/doc/JsonPit_RELEASE_NOTES_4.2.2.md)
