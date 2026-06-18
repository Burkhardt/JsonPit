# JsonPit 3.10.2 Release Notes

## Summary

- Releases `JsonPit` version `3.10.2`.
- Adds and tightens concurrency regression test coverage.
- Aligns fallback package references to `OsLibCore 3.10.2` and `RaiUtils 3.10.2`.
- Refreshes current package metadata, docs, and class-diagram release markers for the coordinated package line.

## Validation

- `dotnet build JsonPit.csproj --nologo -v minimal`
- NuGet publishing remains wired through the parent sequential release chain and the tag-triggered `publish-nuget.yml` workflow.
