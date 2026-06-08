# JsonPit 3.10.0 Release Notes

## Summary

- Releases `JsonPit` version `3.10.0`.
- Aligns fallback package references to `OsLibCore 3.10.0` and `RaiUtils 3.10.0`.
- Refreshes package metadata, docs, and class diagram release markers for the coordinated minor line.
- No public API changes from `3.9.1`.

## Validation

- `dotnet build JsonPit.csproj --nologo -v minimal`
- NuGet publishing remains wired through the tag-triggered `publish-nuget.yml` workflow.
