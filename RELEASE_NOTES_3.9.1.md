# JsonPit 3.9.1 Release Notes

## Summary

- Releases `JsonPit` version `3.9.1`.
- Aligns fallback package references to `OsLibCore 3.9.1` and `RaiUtils 3.9.1`.
- Refreshes package metadata, docs, and class diagram release markers for the coordinated patch line.
- No public API changes from `3.9.0`.

## Validation

- `dotnet build JsonPit.csproj --nologo -v minimal`
- NuGet publishing is handled by the tag-triggered `publish-nuget.yml` workflow.
