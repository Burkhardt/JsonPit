# JsonPit 3.11.1 Release Notes

## Summary

- Releases `JsonPit` version `3.11.1`.
- Aligns fallback package references to `OsLibCore 3.11.1` and `RaiUtils 3.11.1`.
- Carries forward the current concurrency coverage, packaged docs, and class-diagram release markers for the coordinated package line.
- Fixes ticket ownership to use participant identity (`Machine-Subscriber`) so same-machine peers (for example `Neo-pits` and `Neo-AfricaStage.Api`) no longer collapse into one master owner.
- Ensures non-owner writers within the active ticket window persist change files instead of writing the canonical pit directly.
- Adds regression coverage for same-machine API/CLI ticket-window handoff and change-file fallback behavior.

## Validation

- `dotnet build JsonPit.csproj --nologo -v minimal`
- NuGet publishing remains wired through the parent sequential release chain and the tag-triggered `publish-nuget.yml` workflow.
