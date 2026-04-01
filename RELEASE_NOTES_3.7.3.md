# JsonPit 3.7.3 Release Notes

**Date:** March 31, 2026
**Scope:** Full code review and rewrite of `JsonPit.cs` — correctness, thread safety, cloud drive safety, and structural improvements.

---

## Summary

This release is the result of a comprehensive review of `JsonPit.cs` conducted collaboratively between Rainer and Claude (Anthropic). Every class in the file was examined for correctness, thread safety, adherence to OsLib conventions, and compatibility with cloud-synced filesystems. The changes strengthen JsonPit's core promise of *asynchronous persistence with eventual durability* across multiple machines sharing a cloud-synced filesystem.

---

## Bug Fixes

### `GetLatestItemChanged` returned the oldest item, not the newest

The previous implementation sorted descending then called `.Last()`, which returned the minimum rather than the maximum. Replaced with `.Max()` for clarity and correctness. Method also renamed from `GetLastestItemChanged` to `GetLatestItemChanged` (typo fix).

### `DiskHasNewerChanges` renamed for clarity

Formerly `FileHasChangedOnDisk`. The logic was correct — returns `false` if the file doesn't exist on disk, and `true` if the disk timestamp is newer than memory — but the old name suggested "has the file changed" rather than "does disk have newer data than memory."

### Duplicate `CreateChangeFile` methods consolidated

Two `CreateChangeFile` methods existed: a private one writing `.json` and a public one writing `.pit` in nested subdirectories. Consolidated into a single public method that writes `.json` files flat in the `Changes` directory. The method accepts an optional `machineName` parameter for testability (simulating change files from other machines).

### `EnumerateChangeFiles` now searches for `*.json`

Previously searched for `*.pit`, which was inconsistent with the new `CreateChangeFile` implementation. Renamed from `EnumerateChangePitFiles` to `EnumerateChangeFiles` and updated the search pattern to `*.json`.

### `Invalid()` uses `.Any()` instead of `.Count() > 0`

Minor but avoids full enumeration when only existence is needed.

---

## Thread Safety Improvements

### `_locker` and `usingPersistence` moved from static to instance-level

Previously, all `Pit` instances across the entire process shared a single lock and semaphore. This meant unrelated Pit files would block each other — including the read-only compare instance created inside `CreateChangeFiles`. Both are now instance fields.

### `MasterFlagFile.Locker` moved from static to instance-level

Same issue as above. The lock is still used to serialize writes to individual flag files, but no longer blocks unrelated flag file instances.

### Redundant inner lock removed from `CreateChangeFiles`

`Save()` already acquires `_locker` before calling `CreateChangeFiles()`. The inner `lock(_locker)` was redundant (though not harmful due to `Monitor` reentrancy). Removed for clarity.

### `PitItems.MaxCount` changed to `init` setter

`PitItems` is designed as an immutable type (all mutation returns new instances via `Push`). The `MaxCount` property had a public setter that broke this contract. Changed to `init` so it can only be set during construction.

---

## Cloud Drive Safety

### `Store` no longer uses tmp-file-then-rename pattern

**This is a critical change.** The previous implementation wrote to a temporary file and then moved it to replace the original — a pattern that is standard for local filesystems but dangerous on cloud-synced directories.

When a file is deleted (or renamed away) and a new file appears in its place, the cloud provider sees the original file disappear. The milliseconds between deletion and replacement are often enough for the provider to detect a conflict, producing duplicate files like `"MyJsonPit (1).pit"`. This has been confirmed through years of production experience with Dropbox-backed infrastructure.

The fix: write directly to the original file handle via `TextFile`. The file never disappears from the cloud provider's perspective. The master/client philosophy already prevents simultaneous writes from multiple machines, so the atomicity guarantee of rename is unnecessary.

**This pattern must never be reintroduced in any code that writes to cloud-synced filesystem areas.**

### `ExportJson` updated to use `TextFile`

Previously used `File.WriteAllText` directly. Now uses `TextFile` consistent with the OsLib abstraction layer and cloud drive safety.

---

## Structural Improvements

### `PitItem` implements `IEquatable<PitItem>`

The previous `PitItemExtensions.Equals` extension method shadowed `object.Equals` and was never called as an extension — only explicitly via `PitItemExtensions.Equals(d1, d2)`. Equality is now implemented directly on `PitItem` with proper `Equals`, `GetHashCode`, and `IEquatable<PitItem>` support.

**Note:** `GetHashCode` uses exact ticks (not fuzzy `isLike` matching) to maintain the contract that equal objects must produce equal hash codes. The `isLike` extension remains available for explicit use cases that need timestamp tolerance.

### `ChangeDir` renamed to `ChangesDir` with lazy initialization

The property previously called `mkdir()` on every access — surprising side-effect behavior for a property getter. Now uses a cached backing field, creating the directory only on first access.

### `Pit` implements `IDisposable`, replacing the finalizer

Finalizers run on the GC thread non-deterministically. Calling `Save` from `~Pit()` could fail silently or encounter already-collected dependencies. The class now implements `IDisposable` with the standard dispose pattern. The finalizer remains as a safety net but only calls `Dispose(false)`.

### `MergeChanges` deletes individual files, not directory trees

Previously used `rmdir(depth: 10, deleteFiles: true)` on the parent directory of each change file, which could delete the entire `Changes` directory including pending change files from other machines. Now deletes individual change files after processing (master only, after 600-second grace period).

### `Item` class marked `[Obsolete]`

`Item` and `PitItem` had nearly identical interfaces (Id, Modified, Deleted, Note, Dirty, Delete, Invalidate, Validate) but no inheritance relationship. `PitItem` extends `JObject` and is strictly more capable. `Item` is now marked obsolete with guidance to use `PitItem` instead.

### `CompareToOtherHistory` rewritten for correctness

The original implementation used `Enumerable.Except` which relied on reference equality for `PitItem` objects (since `PitItem` extends `JObject`). This meant the diff would almost always return everything from the left side regardless of actual differences. The rewrite uses an explicit `HashSet<(string Id, DateTimeOffset Modified)>` for fast, correct identity matching.

---

## OsLib Compliance

JsonPit now uses OsLib abstractions (`RaiFile`, `RaiPath`, `TextFile`, `RaiDir`) instead of `System.IO` direct calls wherever possible. Three remaining `System.IO` usages are marked with `// TODO: Rainerquest` for future OsLib additions:

- `System.IO.FileInfo` for `LastWriteTimeUtc` in `GetFileChanged()`
- `System.IO.Directory.GetFiles` in `EnumerateChangeFiles()`
- `System.IO.File.SetLastWriteTimeUtc` in `Store()`

---

## Test Impact

The following test updates were required (applied by Codex agent):

| Test File | Change |
|---|---|
| `PitChangeMergeTests` | `ChangeDir` → `ChangesDir`; `.pit` assertions → `.json`; subdirectory assertions → flat file |
| `CloudRemoteSyncTests` | `ChangeDir` → `ChangesDir` |
| `PitSnapshotTests` | `ExportJson(string)` → `ExportJson(RaiPath)` |
| `PitAddConcurrencyTests` | `MaxCount` post-construction assignment → `PitItems.Create(key, 0)` |

All tests pass after updates. Build succeeds with zero errors.

---

## Architecture Reminder

JsonPit follows a model of **asynchronous persistence with eventual durability**:

- A change is not guaranteed to be durably visible everywhere immediately after the local write returns.
- The system is designed so that changes become durably visible over time.
- The master/client philosophy ensures only the master writes the main pit file; all other machines persist their state as ChangeFiles in the `Changes` subdirectory.
- The master consumes and deletes ChangeFiles during `MergeChanges`, writing the consolidated state back to the pit file.
- Convergence matters more than instant global visibility.

---

## Migration Notes

- If you access `ChangeDir` on a `Pit` instance, rename to `ChangesDir`.
- If you call `CreateChangeFile` with a machine name parameter, the method is now public with the signature `CreateChangeFile(PitItem item, string machineName = null)`.
- If you set `MaxCount` on a `PitItems` instance after construction, pass it to the constructor or `PitItems.Create(key, maxCount)` instead.
- If you use the `Item` class, plan migration to `PitItem`. `Item` is now `[Obsolete]`.
- If you implement `IDisposable`-aware lifetime management, `Pit` now supports `using` blocks and explicit `Dispose()`.
- Change files are now `.json` format. Any tooling that scans for `.pit` in the Changes directory should be updated.
