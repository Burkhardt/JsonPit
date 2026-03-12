# JsonPit

    Stores json files across servers (synchronized).

## 3.2.1

- Aligns JsonPit with the shared cloud-root contract used across OsLib, RaiUtils, and the upcoming Python companion packages.
- Keeps `Pit` usage stable on Ubuntu by favoring explicit Google Drive configuration through OsLib.

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

- PitItem: `Name`, `Modified`, `Deleted`, `Note`, `SetProperty`, `DeleteProperty`
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
- Use `OSLIB_CLOUD_ROOT_GOOGLEDRIVE` to point at the active Google Drive mount.
- Use `OSLIB_CLOUD_CONFIG` to point at a machine-local `cloudstorage.ini` file when the mount path differs per machine.
- Reuse the same INI keys as OsLib: `dropbox`, `onedrive`, `googledrive` or `google_drive`, `icloud` or `icloud_drive`.

That keeps JsonPit aligned with OsLib in .NET today and with the upcoming Python `OsLib`, `RaiUtils`, and `JsonPit` packages later.

<details>
<summary>Item: Base item with modified tracking and dirty state management.</summary>

- Item: `Name`, `Modified`, `Deleted`, `Delete`, `Valid`, `Validate`, `Invalidate`
</details>

---

@see [GettingStarted.md](GettingStarted.md) for usage examples or checkout the unit tests