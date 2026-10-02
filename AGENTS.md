# JsonPit mutation contract

Before changing JsonPit or writing its usage examples, read `MUTATION_TRACKING.md` and the relevant section of `GettingStarted.md`.

- `Pit[id]` returns the original adopted live `PitItem`; nested access returns its original attached objects. Preserve the `Assert.Same` contract in `JsonPit.Tests/ReferenceTests.cs`.
- Application code should use indexer assignment, `SetProperty`, `Merge`, `DeleteProperty`, or `DeletePropertyPath`. Do not recommend direct `JValue.Value` assignment.
- Mutations append sparse historical fragments. Do not store mutable live objects as historical fragments, replay entire live projections as updates, or rebuild current state in accessors.
- Both tracking modes support normal tracked edits. `TrackedChangesWithFallback` is the default. `TrackedChangesOnly` disables boundary reconciliation. Each instance captures the process default in memory; never persist this setting.
- Preserve this documented limitation exactly: **Unreported edits will receive their history timestamp when detected.** Fallback is eventual-value protection, not a complete chronological audit.
- Use public mutation/replay APIs, not direct writes to the legacy `HistoricItems` dictionary. Read-only historical snapshots must remain detached.
- Verify reference identity, sparse history, and both modes when changing mutation/persistence behavior. The next coordinated release after 4.4.6/4.4.7 is 4.4.8.
