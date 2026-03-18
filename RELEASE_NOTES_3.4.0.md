# JsonPit 3.4.0 Release Notes

## Highlights

- Version bumped to `3.4.0`.
- JsonPit now aligns with the shared `3.4.0` package set used together with `OsLibCore` and `RaiUtilsCore`.
- Public onboarding documentation was expanded substantially in `GettingStarted.md` so another implementation agent can use JsonPit from NuGet packages with the current APIs and usage model.
- The recommended server-side mental model is now documented explicitly as long-lived in-memory pit usage with asynchronous persistence and eventual durability.

## Packaging and Publish Automation

- JsonPit's standalone package build path now works correctly outside the umbrella workspace.
- The project now falls back to NuGet package references for `OsLibCore` and `RaiUtilsCore` when sibling source repositories are not present locally.
- The NuGet publish workflow was aligned with the other package repositories and updated to restore/build `JsonPit.csproj` directly in standalone CI.

## Practical Usage Guidance

- `GettingStarted.md` now covers package installation, current `Pit` and `PitItem` APIs, update patterns, enumeration/query patterns, and a practical `PersonPit` example for OTW / AfricaStage style backend work.
- The docs now describe JsonPit more accurately as a file-backed, synchronized in-memory working model rather than a database-table abstraction.

## Compatibility

- Existing `Pit` / `PitItem` application code remains stable.
- JsonPit remains suitable for cloud-backed shared storage scenarios when used with explicit OsLib configuration.
- Cross-server behavior should be understood as asynchronous persistence with eventual durability rather than immediate real-time synchronization.

## Validation

- The standalone `JsonPit.csproj` restore/build path was validated successfully after the packaging fix.
- JsonPit's publish workflow was corrected so it can run reliably from the standalone repository.