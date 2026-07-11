# JsonPit CURRENT_STATUS

Last updated: 2026-07-11

Current package line: `3.11.4`

## Current Local State

- `PitItem.DeleteProperty(propertyName)` continues to write a top-level `null` marker for the requested property.
- `PitItems.ProjectState(...)` now treats a top-level `null` in the newest matching fragment as an attribute tombstone: the projected object omits that property and older values for the same property stay suppressed.
- A later non-null fragment for the same property reintroduces the property normally.
- The behavior is top-level only. Nested attribute removal remains an application-level replacement of the containing top-level JSON object or array.

## Files Changed In This Slice

- `PitItems.cs`: projection now tracks seen property names and skips `JTokenType.Null` values while still blocking older shadow values.
- `JsonPit.Tests/DeletePropertyProjectionTests.cs`: added acceptance coverage for no null shadow, reload persistence, live-item preservation, partial null fragments, reintroduction, time travel, and the explicit reintroduced-above-tombstone scenario.
- `GettingStarted.md`: documented the supported `DeleteProperty(...)` usage pattern and projection semantics.
- `Requirements.md`: recorded the top-level tombstone projection invariant.

## Validation

- `dotnet test JsonPit/JsonPit.Tests/JsonPit.Tests.csproj --filter FullyQualifiedName~DeletePropertyProjectionTests --nologo -v minimal` passed: `7` succeeded, `0` failed.
- `dotnet test JsonPit/JsonPit.Tests/JsonPit.Tests.csproj --nologo -v minimal` passed: `101` succeeded, `1` skipped, `0` failed.

## Documentation Notes

- No PlantUML update is required for this fix because no public method signatures, class relationships, or ownership boundaries changed.
- `RELEASE_NOTES_3.11.4.md` records the DeleteProperty projection fix for this package release.