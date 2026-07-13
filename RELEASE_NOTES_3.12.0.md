# JsonPit 3.12.0 Release Notes

## Summary

- Releases `JsonPit` version `3.12.0`.
- Aligns fallback package references to `OsLibCore 3.12.0` and `RaiUtils 3.12.0`.
- Carries forward the current concurrency coverage, packaged docs, and class-diagram release markers for the coordinated package line.
- Carries forward `PitItem.DeleteProperty(...)` projection semantics: top-level null fragments act as attribute tombstones, so projected reads omit the deleted property instead of exposing a permanent null shadow.
- No public API changes from `3.11.5`.

## Validation

- `dotnet test JsonPit.Tests/JsonPit.Tests.csproj --nologo -v minimal`
- NuGet publishing remains wired through the parent sequential release chain and the tag-triggered `publish-nuget.yml` workflow.
