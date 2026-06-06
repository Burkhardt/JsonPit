# JsonPit 3.8.12 Release Notes

## Summary

- Releases `JsonPit` version `3.8.12`.
- Aligns fallback package references to `OsLibCore 3.8.12` and `RaiUtils 3.8.12`.
- Refreshes live README links and PlantUML release markers for the coordinated `RAIkeep` patch line.
- No JsonPit API or persistence behavior changes from `3.8.11`.

## Validation

- `dotnet test RAIkeep.slnx --nologo -v minimal`: 243 passed, 0 failed, 1 skipped.
- NuGet publishing is handled by the RAIkeep Sequential NuGet Release Chain.
