# JsonPit API Reference

This document provides a foldable overview of the public JsonPit 4.2.8 API, including accepted CR021 durable cleanup receipts and explicit maintenance.

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

  Model the stable master lease and per-process activity windows using exact process identities, ownership validation, conflict discovery, and synchronized-storage materialization rules.
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

  `Maintain()` inventories pending change/receipt cleanup without mutation. `Maintain(true)` applies canonical reconciliation and eligible change-first/receipt-second retirement. The options form separately authorizes aged PID-window pruning or validated extensionless flag/event repair. Results expose observed, valid, merged, created, retained, malformed, orphaned, active, released, naturally expired, conflict, deferred, repaired, pruned, and removed counts plus failures and canonical persistence.
  </details>
- <details>
  <summary><code>RecoveryStatus</code>, <code>RecoveryStage</code>, and <code>RecoveryRole</code></summary>

  Describe live split-master recovery state, participant role, and progress without requiring filesystem inference by callers.
  </details>
- <details>
  <summary><code>PitAudit</code> and <code>PitAuditEvent</code></summary>

  Read durable recovery events with optional machine and minimum-level filters for diagnostics and the PitSeeder audit command.
  </details>

## Getting started

Practical package setup and examples are in
[GettingStarted.md](https://github.com/Burkhardt/JsonPit/blob/main/GettingStarted.md).
The concurrency and persistence contract is documented in
[CR003](https://github.com/Burkhardt/RAIkeep/blob/main/doc/CR003_RAI_to_RAIkeep_JsonPit-concurrency-contract-and-persistence-races.md).
