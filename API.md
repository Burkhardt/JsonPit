# JsonPit API Reference

This document provides a foldable overview of the public JsonPit 4.3.1 API. JsonPit participates unchanged in the coordinated CR026 line; CR024 deterministic process-flag cleanup, immutable recovery-event compaction, archive-transparent audit reads, accepted CR021 durable cleanup, and CR022 non-creating maintenance inspection remain intact.

## Pit lifecycle and persistence

- <details>
  <summary><code>Pit</code>: live eventually persistent JSON store</summary>

  Loads and coordinates one live public instance per canonical pit path, exposes enumeration and mutation operations, persists accepted history, observes synchronized-storage changes, and implements the CR003 disposal durability boundary.
  </details>
- <details>
  <summary><code>JsonPitBase</code>: shared pit file and serialization foundation</summary>

  Carries the pit path/file boundary, serialization configuration, version information, and file-versus-memory change-time behavior used by concrete pits.
  </details>
- <details>
  <summary><code>JsonPitPersistenceException</code> and <code>PitInstanceConflictException</code></summary>

  Report validated persistence failures and attempts to open a second live public pit for the same canonical path.
  </details>

## Items and history

- <details>
  <summary><code>PitItem</code>, <code>PitItems</code>, and <code>TimestampedValue</code></summary>

  Represent canonical item identities, timestamped fragments, property-level history, merging, filtering, equality, validation, and recursive tombstone projection. `PitItem.Merge(JObject)` retains null tombstones, `DeleteProperty(...)` addresses a literal top-level name, and `DeletePropertyPath(...)` explicitly traverses dot-delimited nested properties.
  </details>
- <details>
  <summary><code>Item</code> and <code>Compare</code></summary>

  Provide the compatibility object model and JSON- or property-based matching used by established consumers.
  </details>

## Coordination and recovery

- <details>
  <summary><code>MasterFlagFile</code> and <code>ProcessFlagFile</code></summary>

  Model the stable master lease and per-process activity windows using exact process identities, ownership validation, conflict discovery, and synchronized-storage materialization rules. `TryReleaseCurrentProcess()` deletes only the exact owned PID flag through `RaiFile.rm()`; it never changes or removes `Master.flag`.
  </details>
- <details>
  <summary><code>ChangeFile</code></summary>

  Produces canonical, hashed, collision-safe change artifacts and validates their payloads before replay.
  </details>
- <details>
  <summary><code>ReceiptFile</code></summary>

  Stores one immutable UTC round-trip timestamp beside its associated hashed change file. The receipt has the same complete logical stem and the `.receipt` extension; reopening it preserves its original content, `Time`, and write timestamp.
  </details>
- <details>
  <summary><code>Pit.Maintain(...)</code>, <code>PitMaintenanceOptions</code>, and <code>PitMaintenanceResult</code></summary>

  `Maintain()` inventories pending change/receipt cleanup without mutation. It checks the non-creating canonical parent first, so a missing target returns a deferred result without creating its directory. `Maintain(true)` applies canonical reconciliation and eligible change-first/receipt-second retirement only for an existing target. The options form separately authorizes aged PID-window pruning, validated extensionless flag/event repair, or immutable event archiving through `ArchiveEvents`. Archive preview/apply results expose loose/archive counts, exact name, UTC range, reused archives, removals, deferrals, and failures in addition to the established maintenance inventory. The public result constructor accepts the intended pit filename and applied state so typed callers can represent skipped missing targets without opening a `Pit`.
  </details>
- <details>
  <summary><code>RecoveryStatus</code>, <code>RecoveryStage</code>, and <code>RecoveryRole</code></summary>

  Describe live split-master recovery state, participant role, and progress without requiring filesystem inference by callers.
  </details>
- <details>
  <summary><code>PitAudit</code> and <code>PitAuditEvent</code></summary>

  `PitAudit.Read(...)` reads loose and archived durable recovery events as one identity-deduplicated logical history with optional machine and minimum-level filters. `PitAudit.Inspect(...)` additionally returns physical invalid/archive/conflict diagnostics through `PitAuditReadResult`; both paths are strictly read-only and extract nothing.
  </details>

## Getting started

Practical package setup and examples are in
[GettingStarted.md](https://github.com/Burkhardt/JsonPit/blob/main/GettingStarted.md).
The concurrency and persistence contract is documented in
[CR003](https://github.com/Burkhardt/RAIkeep/blob/main/doc/CR003_RAI_to_RAIkeep_JsonPit-concurrency-contract-and-persistence-races.md).
