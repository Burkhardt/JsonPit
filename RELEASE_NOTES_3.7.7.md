# JsonPit 3.7.7 Release Notes

## Summary

- Patch release for `JsonPit` version `3.7.7`.
- Aligns fallback package references with `OsLibCore 3.7.7` and `RaiUtils 3.7.7`.
- Refreshes current docs and class-diagram headers without changing the `PitItem.Id` contract or persistence behavior.

## Documentation

- Updated `README.md`, `GettingStarted.md`, and `Requirements.md` to the `3.7.7` package line.
- Refreshed `JsonPit-ClassDiagram.puml` so the current diagram header matches the live release line.

## Validation

- `dotnet test RAIkeep.slnx --nologo -v minimal`
- Result: 214 passed, 0 failed, 0 skipped.