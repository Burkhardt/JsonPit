# JsonPit 3.11.4 Release Notes

## Summary

- Releases `JsonPit` version `3.11.4`.
- Aligns fallback package references to `OsLibCore 3.11.4` and `RaiUtils 3.11.4`.
- Carries forward the current concurrency coverage, packaged docs, and class-diagram release markers for the coordinated package line.
- No public API changes from `3.11.3`.

## Validation

- `dotnet build JsonPit.csproj --nologo -v minimal`
- NuGet publishing remains wired through the parent sequential release chain and the tag-triggered `publish-nuget.yml` workflow.
