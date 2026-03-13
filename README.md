# JsonPit

    Stores json files across servers (synchronized).

## 3.3.0

- Aligns JsonPit with OsLib's `Os.Config` and `osconfig.json` contract.
- Keeps `Pit` usage stable by using config-driven cloud-root resolution rather than machine-local override variables.

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
- Use `osconfig.json` to point `cloud.googledrive` at the active Google Drive mount.
- Keep that file at `~/.config/RAIkeep/osconfig.json` on macOS/Linux or `%APPDATA%\RAIkeep\osconfig.json` on Windows.
- Reuse the same `cloud` keys as OsLib: `dropbox`, `onedrive`, `googledrive`, `icloud`.

That keeps JsonPit aligned with OsLib in .NET today and with the upcoming Python `OsLib`, `RaiUtils`, and `JsonPit` packages later.

<details>
<summary>Item: Base item with modified tracking and dirty state management.</summary>

- Item: `Name`, `Modified`, `Deleted`, `Delete`, `Valid`, `Validate`, `Invalidate`
</details>

---

@see [GettingStarted.md](GettingStarted.md) for usage examples or checkout the unit tests

## release notes

- Current release notes: [RELEASE_NOTES_3.3.0.md](RELEASE_NOTES_3.3.0.md)