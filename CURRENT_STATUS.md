# JsonPit CURRENT_STATUS

Last updated: 2026-07-13

Current package line: `3.11.5`

## Current Local State

- `PitItem.DeleteProperty(propertyName)` continues to write a top-level `null` marker for the requested property.
- `PitItems.ProjectState(...)` continues to treat a top-level `null` in the newest matching fragment as an attribute tombstone: the projected object omits that property and older values for the same property stay suppressed.
- A later non-null fragment for the same property reintroduces the property normally.
- The behavior is top-level only. Nested attribute removal remains an application-level replacement of the containing top-level JSON object or array.
- Fallback package references now align to `OsLibCore 3.11.5` and `RaiUtils 3.11.5`.
- Packaged docs, release notes, and the class-diagram release marker are aligned with the `3.11.5` patch line.

## Files Changed In This Slice

- `JsonPit.csproj`: bumped package metadata to `3.11.5` and aligned fallback package references to `OsLibCore 3.11.5` and `RaiUtils 3.11.5`.
- `README.md`, `GettingStarted.md`, and `Requirements.md`: refreshed the current package-line wording and install examples to `3.11.5`.
- `JsonPit-ClassDiagram.puml` and `JsonPit-ClassDiagram.svg`: refreshed the tracked release marker render for the new patch line.
- `RELEASE_NOTES_3.11.5.md`: records the carried-forward DeleteProperty tombstone behavior for this package release.

## Validation

- `dotnet test JsonPit/JsonPit.Tests/JsonPit.Tests.csproj --filter FullyQualifiedName~DeletePropertyProjectionTests --nologo -v minimal` passed: `7` succeeded, `0` failed.
- `dotnet test JsonPit/JsonPit.Tests/JsonPit.Tests.csproj --nologo -v minimal` passed: `101` succeeded, `1` skipped, `0` failed.

## Documentation Notes

- The class diagram release marker was refreshed for `3.11.5`, but no public method signatures, class relationships, or ownership boundaries changed.
- `RELEASE_NOTES_3.11.5.md` records the carried-forward DeleteProperty projection behavior for this package release.
