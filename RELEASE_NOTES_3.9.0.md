# JsonPit 3.9.0 Release Notes

## Summary

- Releases `JsonPit` version `3.9.0`.
- Aligns fallback package references to `OsLibCore 3.9.0` and `RaiUtils 3.9.0`.
- Refreshes markdown docs and class-diagram release markers for the coordinated minor line.
- No JsonPit API changes from `3.8.15`.

## Validation

- `dotnet test JsonPit.Tests/JsonPit.Tests.csproj --nologo -v minimal`
- NuGet publishing is handled by tag-triggered `publish-nuget.yml`.
