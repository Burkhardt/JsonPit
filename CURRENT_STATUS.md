# JsonPit CURRENT_STATUS

Last updated: 2026-08-10

Current package line: `4.0.1` synchronized CR006 patch prep; JsonPit has version/dependency metadata changes only and publication remains pending explicit release authorization

## CR003 Implementation (coordinated 3.13.2)

- State/snapshot gate: concurrent `Add` calls run in shared mode; `Save` captures a brief exclusive, byte-stable snapshot and releases the gate before serialization and cloud I/O. Only fragments in the written snapshot are validated; later additions stay dirty for the next save. The confirmed `SaveInterleavedWithAdds` race is fixed.
- `Master.flag` records exact process identity (`{Machine}-{Subscriber}-{PID}`); only the exact owner renews; a same-participant PID is refused while the owner's window is active and inherits after release/expiry.
- One live public `Pit` per canonical path per process (weak-reference registry, `PitInstanceConflictException`); internal comparison/merge paths use private snapshot readers.
- Validated candidate loads: live state replaced only after a complete parse; transient read-during-write failures retried; bounded exhaustion throws `JsonPitPersistenceException`.
- Collision-safe change files `{Modified.UtcTicks}_{ExactProcessIdentity}_{Sha256}.json` (hash over exact canonical UTF-8 bytes); merge requires hash+parse validation; legacy names still ingested for upgrade.
- Two-stage current-master-only cleanup with a grace measured from successful canonical persistence (`Pit.ChangeFileCleanupGrace`, default 10 min); restart/transfer resets eligibility.
- Live split-master recovery: per-tenure recovery write set, `Master*.flag` watcher plus operation-boundary scans, loser/orphan protocols, live-transfer export, disposal durability boundary; durable canonical-JSON audit events under the pit's `Events` child; `LastRecoveryStatus` + `RecoveryStatusChanged`.
- Fallback package references align to `OsLibCore 4.0.1` and `RaiUtils 4.0.1`.
- Mixed-version caution: pre-3.13.2 processes can fail on 3.13.2 hashed change files; upgrade all participants of a shared pit together.

## Previous Local State (3.13.1)

- `PitItem.DeleteProperty(propertyName)` continues to write a top-level `null` marker for the requested property.
- `PitItems.ProjectState(...)` continues to treat a top-level `null` in the newest matching fragment as an attribute tombstone: the projected object omits that property and older values for the same property stay suppressed.
- A later non-null fragment for the same property reintroduces the property normally.
- The behavior is top-level only. Nested attribute removal remains an application-level replacement of the containing top-level JSON object or array.
- Fallback package references now align to `OsLibCore 3.13.1` and `RaiUtils 3.13.1`.
- Packaged docs, release notes, and the class-diagram release marker are aligned with the `3.13.1` patch line.
- Process activity flags are per process (`{Machine}-{Subscriber}-{PID}.flag`) while master writer ownership retains the stable `{Machine}-{Subscriber}` identity.
- `TryReleaseProcessWindow()` expires only an ownership-verified current-process flag and does not release the master ticket.
- Flag writes use OsLib's in-place save path to avoid OneDrive-sensitive delete/recreate cycles.

## Files Changed In This Slice

- `JsonPit.csproj`: bumped package metadata to `3.13.1` and aligned fallback package references to `OsLibCore 3.13.1` and `RaiUtils 3.13.1`.
- `FlagFile.cs` and `JsonPitBase.cs`: added per-PID activity flags and ownership-verified release while retaining stable master-ticket participants.
- `JsonPit.cs`: refreshes activity timestamps at successful load completion and releases constructor-created windows if construction fails.
- `README.md`, `GettingStarted.md`, and `Requirements.md`: refreshed the current package-line wording and install examples to `3.13.1`.
- `JsonPit-ClassDiagram.puml` and `JsonPit-ClassDiagram.svg`: include the new process-window lifecycle API.
- `doc/JsonPit_RELEASE_NOTES_3.13.1.md`: records the lifecycle API and carried-forward DeleteProperty tombstone behavior.

## Validation

- JsonPit v4.0.1 umbrella validation, including configured cloud and SSH remote scenarios: `146` passed, `0` failed, `0` skipped.
- The two-server split-master scenario (Nkosikazi ↔ Mzansi, OneDrive) passed; artifacts and timing are recorded in the umbrella `doc/JsonPit_RELEASE_NOTES_3.13.2.md`.
- Historical 2026-08-03 state: `103` tests passed excluding `SaveInterleavedWithAdds_SubsequentSavePersistsEveryAcceptedItem`, whose repeated failure motivated CR003; that test now passes repeatedly.

## Documentation Notes

- The class diagram documents `TryReleaseProcessWindow()` plus the process-flag ownership helpers added for `3.13.1`.
- The umbrella `doc/JsonPit-FlagFiles-And-Concurrency.md` now describes the implemented v3.13.2 contract, including the accepted crash/power-loss limitation.
- Release notes: umbrella `doc/JsonPit_RELEASE_NOTES_3.13.2.md` referencing `CR003_RAI_to_RAIkeep_JsonPit-concurrency-contract-and-persistence-races.md`.
