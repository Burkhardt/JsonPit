# JsonPit 3.8.14 Release Notes

## Summary

- Releases `JsonPit` version `3.8.14`.
- Aligns fallback package references to `OsLibCore 3.8.14` and `RaiUtils 3.8.14`.
- Refreshes markdown docs and class-diagram release markers for the coordinated chain.

## Validation

- `dotnet test JsonPit.Tests/JsonPit.Tests.csproj --nologo -v minimal`
- NuGet publishing is handled by tag-triggered `publish-nuget.yml`.
