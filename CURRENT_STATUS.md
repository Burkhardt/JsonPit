# JsonPit CURRENT_STATUS

Last updated: 2026-07-20

Current package line: `3.13.0`

## Current Local State

- `PitItem.DeleteProperty(propertyName)` continues to write a top-level `null` marker for the requested property.
- `PitItems.ProjectState(...)` continues to treat a top-level `null` in the newest matching fragment as an attribute tombstone: the projected object omits that property and older values for the same property stay suppressed.
- A later non-null fragment for the same property reintroduces the property normally.
- The behavior is top-level only. Nested attribute removal remains an application-level replacement of the containing top-level JSON object or array.
- Fallback package references now align to `OsLibCore 3.13.0` and `RaiUtils 3.13.0`.
- Packaged docs, release notes, and the class-diagram release marker are aligned with the `3.13.0` minor line.

## Files Changed In This Slice

- `JsonPit.csproj`: bumped package metadata to `3.13.0` and aligned fallback package references to `OsLibCore 3.13.0` and `RaiUtils 3.13.0`.
- `README.md`, `GettingStarted.md`, and `Requirements.md`: refreshed the current package-line wording and install examples to `3.13.0`.
- `JsonPit-ClassDiagram.puml` and `JsonPit-ClassDiagram.svg`: refreshed the tracked release marker render for the new minor line.
- `RELEASE_NOTES_3.13.0.md`: records the carried-forward DeleteProperty tombstone behavior for this package release.

## Validation

- `dotnet test JsonPit/JsonPit.Tests/JsonPit.Tests.csproj --nologo -v minimal` passed: `101` succeeded, `1` skipped, `0` failed.

## Documentation Notes

- The class diagram release marker was refreshed for `3.13.0`, but no public method signatures, class relationships, or ownership boundaries changed.
- `RELEASE_NOTES_3.13.0.md` records the carried-forward DeleteProperty projection behavior for this package release.
