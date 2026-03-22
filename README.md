# JsonPit

    Stores json files across servers (synchronized).

## Start Here

If you want to use JsonPit 3.5.0 from NuGet in another service or agent workflow, start with [GettingStarted.md](GettingStarted.md).

That guide now covers:

- package setup for `JsonPit`, `RaiUtilsCore`, and `OsLibCore`
- current `Pit` and `PitItem` usage patterns
- the recommended long-lived in-memory server usage pattern
- querying and enumeration patterns that are actually supported
- persistence and synchronized-storage expectations
- a practical `PersonPit` example for OTW / AfricaStage style backend work

## 3.5.0

- Keeps JsonPit aligned with the shared 3.5.0 package set used together with `OsLibCore` and `RaiUtilsCore`.
- The supported cloud-backed provider claim is now `OneDrive`, `GoogleDrive`, and `Dropbox`.
- `PitItem.Id` is now the canonical framework identifier.
- Legacy payloads that still contain `Name` without `Id` are normalized internally to `Id`, and the framework-managed `Name` field is dropped.
- Future use of `Name` as an application-defined custom field remains supported.

## namespace

JsonPit

## classes

<details>
<summary>ItemsBase: Base container holding a key identifier for item groups.</summary>

- ItemsBase: `Key`
</details>

<details>
<summary>JsonPitBase: Common base for pits with config, flags, and persistence helpers.</summary>

- JsonPitBase: `ReadOnly`, `Backup`, `RunningOnMaster`, `MasterUpdatesAvailable`, `ChangeDir`, `JsonFile`
</details>

<details>
<summary>TimestampedValue: Value with an attached timestamp and round-trip string format.</summary>

- TimestampedValue: `Value`, `Time`, `ToString`
</details>

<details>
<summary>MasterFlagFile: Flag file used to track master ownership and last update time.</summary>

- MasterFlagFile: `Originator`, `Time`, `Update`
</details>

<details>
<summary>ProcessFlagFile: Flag file used to track the current process and last update time.</summary>

- ProcessFlagFile: `Process`, `Update`, `CurrentProcessId`
</details>

<details>
<summary>PitItem: JSON-backed item with metadata and change tracking.</summary>

- PitItem: `Id`, `Modified`, `Deleted`, `Note`, `SetProperty`, `DeleteProperty`
</details>

<details>
<summary>PitItemExtensions: Helpers for comparing items and aligning timestamps.</summary>

- PitItemExtensions: `Equals`, `isLike`, `aligned`
</details>

<details>
<summary>PitItems: History stack of PitItem versions for a single key.</summary>

- PitItems: `Push`, `Peek`, `Get`, `Merge`, `Count`
</details>

<details>
<summary>Pit: JsonPit file container with item history and persistence.</summary>

- Pit: `Add`, `Get`, `GetAt`, `Delete`, `Save`, `MergeChanges`, `Keys`
</details>

## cloud root convention

JsonPit resolves cloud-backed storage locations through OsLib, most commonly via `Os.CloudStorageRoot`.

For Ubuntu development machines, especially when Google Drive is mounted through `rclone`, GNOME integration, or a team-specific mount path, prefer explicit configuration instead of probe-only discovery.

Recommended shared contract:
- Use `osconfig.json` to point the supported provider roots `cloud.dropbox`, `cloud.onedrive`, and `cloud.googledrive` at the active synchronized mounts.
- Keep that file at `~/.config/RAIkeep/osconfig.json`.
- Reuse the same `cloud` keys as OsLib: `dropbox`, `onedrive`, `googledrive`.

That keeps JsonPit aligned with OsLib in .NET today and with the upcoming Python `OsLib`, `RaiUtils`, and `JsonPit` packages later.

<details>
<summary>Item: Base item with modified tracking and dirty state management.</summary>

- Item: `Id`, `Modified`, `Deleted`, `Delete`, `Valid`, `Validate`, `Invalidate`
</details>

---

@see [GettingStarted.md](GettingStarted.md) for the practical onboarding guide, or check the unit tests for lower-level API examples.

## release notes

- Latest release notes: [RELEASE_NOTES_3.5.0.md](RELEASE_NOTES_3.5.0.md)