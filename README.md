# JsonPit

## 4.5.12

Coordinated 4.5.12 release; public behavior is aligned with the synchronized platform.

Latest release notes: [JsonPit 4.5.12 release notes](https://github.com/Burkhardt/RAIkeep/blob/main/doc/releases/v4.x/JsonPit_RELEASE_NOTES_4.x.md#4512).

## Previous Versions

For release notes and changelogs of earlier releases, see the [JsonPit 4.x Release Notes](https://github.com/Burkhardt/RAIkeep/blob/main/doc/releases/v4.x/JsonPit_RELEASE_NOTES_4.x.md).

## Installation

### .NET CLI

```bash
dotnet add package JsonPit --version 4.5.12
```

### PackageReference

```xml
<PackageReference Include="JsonPit" Version="4.5.12" />
```

JsonPit stores JSON documents with value history across processes, machines, and servers using [asynchronous persistence with eventual durability](https://github.com/Burkhardt/RAIkeep/blob/main/MANIFESTO.md#asynchronous-persistence-with-eventual-durability). Change requests and release notes are kept in the RAIkeep [documentation archive](https://github.com/Burkhardt/RAIkeep/tree/main/doc).

**CLI tools:** use [pits](https://github.com/Burkhardt/PitSeeder) or [jpit](https://pypi.org/project/jsonpit/) to seed, export, inspect, and maintain JsonPits. Use [amafu init](https://github.com/Burkhardt/Amafu) to detect cloud drives and create the shared RAIkeep configuration.

## Start Here

For the 4.5.12 live-reference behavior, read [MUTATION_TRACKING.md](https://github.com/Burkhardt/JsonPit/blob/main/MUTATION_TRACKING.md). It defines the supported mutation API, per-instance tracking modes, and detection-time timestamps for fallback edits. Agents and contributors should also read [AGENTS.md](https://github.com/Burkhardt/JsonPit/blob/main/AGENTS.md).

If you want to use JsonPit 4.5.12 from NuGet in another service or agent workflow, start with [GettingStarted.md](https://github.com/Burkhardt/JsonPit/blob/main/GettingStarted.md).

That guide now covers:

- package setup for `JsonPit`, `RaiUtils`, and `OsLibCore`
- current `Pit` and `PitItem` usage patterns
- the recommended long-lived in-memory server usage pattern
- querying and enumeration patterns that are actually supported
- persistence and synchronized-storage expectations
- a practical `PersonPit` example for OTW / AfricaStage style backend work

JsonPit's durable recovery events and the strictly read-only CLI inspection path
are documented in the
[`pits audit` operational manual](https://github.com/Burkhardt/RAIkeep/blob/main/doc/PITS-AUDIT.md).

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
- Explicit release verifies the current process identity and removes the exact flag through `RaiFile.rm()`, including cloud disappearance waiting.
- Process activity windows and master writer tickets are separate; releasing the former never releases the latter.

### PitItem: JSON-backed item with metadata and change tracking.

- PitItem: `Id`, `Modified`, `Deleted`, `Note`, `SetProperty`, `Merge`, `DeleteProperty`, `DeletePropertyPath`
- Live updates must be sparse new fragments (`new PitItem(existingId)`); do not
  re-add a projected item returned by `Pit.Get(...)` or the indexer.
- `SetProperty(...)`/`Merge(...)` reject top-level `Id`, `Modified`, and
  `Deleted`. Property tombstones reject the same engine-managed names.
- `AddHistorical(...)` is reserved for trusted canonical/change replay where
  historic `Modified` and `Deleted` values must be retained.

### PitItemExtensions: Helpers for comparing items and aligning timestamps.

- PitItemExtensions: `Equals`, `isLike`, `aligned`

### PitItems: History stack of PitItem versions for a single key.

- PitItems: `Push`, `Peek`, `Get`, `Merge`, `Count`

### Pit: JsonPit file container with item history and persistence.

- Pit: `Add`, `Get`, `GetAt`, `Delete`, `Save`, `MergeChanges`, `Keys`

## Maintenance lifecycle

Normal writable `Pit` construction with `autoload: true` preserves JsonPit's
original **master opening performs cleanup** behavior. Construction loads the
canonical pit and calls `MergeChanges()` (applying `Maintain(...)`): the exact
current master merges valid change files, persists any newly accounted history,
creates or reuses immutable cleanup receipts, and retires eligible change-file /
receipt pairs.

Cleanup is deliberately a later-pass protocol. A newly created receipt starts a
ten-minute grace period, and deletion can occur only during a subsequent
maintenance pass after that grace has elapsed. JsonPit does not install a
background timer. Consequently, a service that opens a pit once and keeps it
open may need to call `Pit.Maintain(new PitMaintenanceOptions { Apply = true })`
periodically; otherwise the next normal master opening performs the later pass.

Expired process-window flags are a separate diagnostic-retention concern. They
are inventoried by maintenance but are never deleted implicitly when a master
opens a pit. Their removal requires explicit apply, pruning, and age options.
The `pits` CLI can combine ordinary WWWA maintenance and process-flag pruning in
one invocation:

```bash
pits maintain --wwwa -c OneDrive -r AIA \
  --apply \
  --prune-process-flags \
  --older-than 01:00:00 \
  --json
```

The combined command already performs ordinary change/receipt maintenance; a
second `pits maintain --apply` call is unnecessary. Recovery event files are a
durable audit trail and are not pruned by this operation.

Event-file compaction is a separate explicit operation. Setting
`ArchiveEvents = true` previews the exact immutable archive; adding `Apply = true`
creates it in the existing `Events` directory and retires only the loose copies
whose exact bytes were validated in the archive:

```csharp
var preview = pit.Maintain(new PitMaintenanceOptions { ArchiveEvents = true });
var applied = pit.Maintain(new PitMaintenanceOptions
{
    Apply = true,
    ArchiveEvents = true
});
```

The archive name is based on the oldest and newest validated event content times
in UTC, for example `Events_20260804-0118_to_20260910-1643.zip`. Existing
same-name archives are immutable: identical content is reusable, while corrupt
or different content is reported and all loose evidence is retained. Audit reads
remain logically unchanged because `PitAudit` combines validated loose and
archived events without extracting files.

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
