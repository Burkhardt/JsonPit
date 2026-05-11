# JsonPit 3.8.1 Release Notes

## Summary

- Coordinated release for `JsonPit` version `3.8.1`.
- Aligns fallback package references with `OsLibCore 3.8.1` and `RaiUtils 3.8.1`.
- Keeps the packaged README in the NuGet payload.
- Refreshes current docs and class-diagram headers without changing the `PitItem.Id` contract or persistence behavior.

## Documentation

- Updated `README.md`, `GettingStarted.md`, and `Requirements.md` to the `3.8.1` package line.
- Refreshed `JsonPit-ClassDiagram.puml` so the current diagram header matches the live release line.

## Validation

- `dotnet test JsonPit.slnx --nologo -v minimal`
- Result: 84 passed, 0 failed, 0 skipped.