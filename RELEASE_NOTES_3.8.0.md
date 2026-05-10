# JsonPit 3.8.0 Release Notes

## Summary

- Coordinated release for `JsonPit` version `3.8.0`.
- Aligns fallback package references with `OsLibCore 3.8.0` and `RaiUtils 3.8.0`.
- Keeps the packaged README in the NuGet payload.
- Refreshes current docs and class-diagram headers without changing the `PitItem.Id` contract or persistence behavior.

## Documentation

- Updated `README.md`, `GettingStarted.md`, and `Requirements.md` to the `3.8.0` package line.
- Refreshed `JsonPit-ClassDiagram.puml` so the current diagram header matches the live release line.

## Validation

- `dotnet test JsonPit.slnx --nologo -v minimal`
- Result: 83 passed, 0 failed, 1 skipped.
