# JsonPit 3.12.0 Release Notes

## Summary

- Releases `JsonPit` version `3.12.0`.
- Aligns fallback package references to `OsLibCore 3.12.0` and `RaiUtils 3.12.0`.
- Carries forward the participant-identity ticket ownership behavior, current concurrency coverage, packaged docs, and class-diagram release markers for the coordinated package line.
- No public API changes from `3.11.4`.

## Validation

- `dotnet build JsonPit.csproj --nologo -v minimal`
- NuGet publishing remains wired through the parent sequential release chain and the tag-triggered `publish-nuget.yml` workflow.
