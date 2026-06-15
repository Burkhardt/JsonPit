# JsonPit 3.10.1 Release Notes

## Summary

- Releases `JsonPit` version `3.10.1`.
- Aligns fallback package references to `OsLibCore 3.10.1` and `RaiUtils 3.10.1`.
- Refreshes current package metadata, docs, and class diagram release markers for the coordinated package line.
- No public API changes from `3.10.0`.

## Validation

- `dotnet build JsonPit.csproj --nologo -v minimal`
- NuGet publishing remains wired through the parent sequential release chain and the tag-triggered `publish-nuget.yml` workflow.
