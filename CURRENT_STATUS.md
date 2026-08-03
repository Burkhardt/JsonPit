# JsonPit CURRENT_STATUS

Last updated: 2026-08-03

Current package line: `3.13.1`

## Current Local State

- `PitItem.DeleteProperty(propertyName)` continues to write a top-level `null` marker for the requested property.
- `PitItems.ProjectState(...)` continues to treat a top-level `null` in the newest matching fragment as an attribute tombstone: the projected object omits that property and older values for the same property stay suppressed.
- A later non-null fragment for the same property reintroduces the property normally.
- The behavior is top-level only. Nested attribute removal remains an application-level replacement of the containing top-level JSON object or array.
- Fallback package references now align to `OsLibCore 3.13.1` and `RaiUtils 3.13.1`.
- Packaged docs, release notes, and the class-diagram release marker are aligned with the `3.13.1` patch line.
- Process activity flags are per process (`{Machine}-{Subscriber}-{PID}.flag`) while master writer ownership retains the stable `{Machine}-{Subscriber}` identity.
- `TryReleaseProcessWindow()` expires only an ownership-verified current-process flag and does not release the master ticket.
- Flag writes use OsLib's in-place save path to avoid OneDrive-sensitive delete/recreate cycles.

## Files Changed In This Slice

- `JsonPit.csproj`: bumped package metadata to `3.13.1` and aligned fallback package references to `OsLibCore 3.13.1` and `RaiUtils 3.13.1`.
- `FlagFile.cs` and `JsonPitBase.cs`: added per-PID activity flags and ownership-verified release while retaining stable master-ticket participants.
- `JsonPit.cs`: refreshes activity timestamps at successful load completion and releases constructor-created windows if construction fails.
- `README.md`, `GettingStarted.md`, and `Requirements.md`: refreshed the current package-line wording and install examples to `3.13.1`.
- `JsonPit-ClassDiagram.puml` and `JsonPit-ClassDiagram.svg`: include the new process-window lifecycle API.
- `doc/JsonPit_RELEASE_NOTES_3.13.1.md`: records the lifecycle API and carried-forward DeleteProperty tombstone behavior.

## Validation

- On 2026-08-03, `103` JsonPit tests passed in the run excluding `SaveInterleavedWithAdds_SubsequentSavePersistsEveryAcceptedItem`. The OneDrive remote-sync test initially observed its known file-materialization timing race, then passed on an immediate isolated rerun in 68 seconds.
- An immediate isolated rerun failed again in `ConcurrentDictionary.ICollection.CopyTo(...)`; see `doc/JsonPit_CR_concurrency-for-next-release.md`.

## Documentation Notes

- The class diagram documents `TryReleaseProcessWindow()` plus the process-flag ownership helpers added for `3.13.1`.
- `doc/JsonPit_RELEASE_NOTES_3.13.1.md` records both the lifecycle change and the carried-forward DeleteProperty projection behavior.
