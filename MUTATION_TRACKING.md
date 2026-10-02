# Live references and mutation tracking

This contract is introduced for the next coordinated release, 4.4.8. It applies to C# JsonPit. See `GettingStarted.md` for application examples and `JsonPit.Tests/ReferenceTests.cs` for the minimal identity requirement.

## Normal application and agent-generated code

Use indexers and the public mutation methods. They record accepted changes immediately in in-memory history:

```csharp
var who = new JObject { ["Owner"] = "Unknown" };
var item = new PitItem("Ego") { ["Who"] = who };
pit.Add(item);

// These are the same objects supplied above.
var ego = pit["Ego"];
ego["Instagram"] = "@Dr2RAI";
who["Owner"] = "Rainer";
ego.SetProperty(new { Role = "Musician", Status = "Active" });
ego.DeleteProperty("Instagram");
ego.DeletePropertyPath("Who.Owner");
pit.Save();
```

The live object is not added back after editing. Each supported logical mutation creates a sparse fragment with the identity and changed attributes, plus engine metadata. `SetProperty`/`Merge` group their patch into one accepted mutation. Objects merge recursively, property nulls are tombstones, and arrays are stored as replacement values. A later `Add` of a sparse fragment for the same ID updates the established live item rather than adopting the fragment as a replacement live root.

Do not set or remove `Id`, `Modified`, or `Deleted` as ordinary attributes. Use `RenameId`, `Pit.Delete`, or the live item's `Delete` for identity/lifecycle operations. Genuine replay uses `AddHistorical`; it preserves historical timestamps.

For normal code, use `who["Owner"] = "Rainer"`. Do not use `((JValue)who["Owner"]).Value = "Rainer"`: Newtonsoft raises no notification for that setter. Imported anonymous objects are value input, not mutable CLR objects that JsonPit writes back into.

## Two modes, selected per in-memory instance

```csharp
// Default for CLI and general use:
Pit.DefaultMutationTrackingMode = MutationTrackingMode.TrackedChangesWithFallback;

// Server startup, before constructing any Pits:
Pit.DefaultMutationTrackingMode = MutationTrackingMode.TrackedChangesOnly;
```

`pit.TrackingMode` captures the static default at construction and cannot be changed. The setting lasts only for that C# object's lifetime in memory. It is never written into pit files, fragments, flags, or receipts. A server and CLI can use different policies on the same data. A later change to the static default affects only subsequently constructed instances.

| Mode | Normal tracked mutations | Boundary comparisons |
| --- | --- | --- |
| `TrackedChangesOnly` | Accepted immediately | Disabled. Unreported edits are unsupported and not guaranteed persistence. |
| `TrackedChangesWithFallback` (default) | Accepted immediately | Detects differences from the last accepted baseline. |

Choose `TrackedChangesOnly` when the application consistently follows the tracked mutation contract. It removes fallback scans, not the processing cost of normal tracking. Short process lifetime alone is not a guarantee of prompt persistence or detection.

## Fallback semantics and consistency

**Unreported edits will receive their history timestamp when detected.**

The original live object remains the object seen by callers. A separate baseline represents its last accepted state. Historical fragments own independent snapshots; editing the live object cannot rewrite their values. The baseline is refreshed when a change is accepted into history, not only when disk I/O succeeds. A failed Save therefore leaves accepted fragments dirty and available for retry.

Fallback comparison happens before Save/Store, successful Load state replacement, Reload, current-state export, recovery capture, and explicit writable disposal. Before Add, replay/merge, or a tracked helper updates an existing item, the item's fallback differences are accepted first. Notification processing may also detect previously unreported changes. Accessors themselves do not trigger fallback scans or rebuild projections.

Consequences of bypassing tracked operations:

- Live reads can show an unreported value while `GetAt` and recorded history still show the last accepted value.
- Several unreported assignments can be coalesced. Intermediate values, including a change followed by a return to the baseline, cannot be reconstructed.
- An older edit detected later gets a later `Modified`. It can therefore take precedence over a competing change when the history comparator orders by timestamp. This is not a complete audit of edit time.
- Fallback compares live data and retains a baseline. Large working sets and frequent boundaries increase CPU, allocation, and memory cost. Mode 1 avoids the boundary scans; both modes still perform normal tracking work.

Immediate tracking means acceptance into memory, not immediate durable storage or cloud synchronization. Continue to use Save and the existing explicit-disposal durability boundaries. Finalizers perform no persistence. Mode 2 does not make unsaved memory survive a crash.

## Where values are established

JsonPit folds deterministically ordered history at acceptance/load/merge boundaries and updates the existing live graph. The fold uses LINQ to select the relevant entity lifetime, reverse newest-first fragments, and aggregate properties oldest first. Recursive property application handles tombstones and nested siblings. Repeated current lookups return registered references and do not re-project history.

Historical `GetAt` results and exposed `History`/`ValuesOverTime` values are detached snapshots. They are for inspection, not live mutation. The legacy public `HistoricItems` dictionary is an engine-level compatibility surface; application and agent code must not insert, replace, or remove its entries. Use the public Add/replay/merge APIs so the live graph and history stay coherent.

Nested references remain valid during in-place changes and updates to siblings. Replacing/removing a subtree detaches the old nodes; mutating detached JSON no longer edits the Pit. Do not reuse a deleted/obsolete root or attach one live `PitItem` to a second Pit. Import an explicit detached client payload when creating another entity.

As with ordinary mutable `JObject` graphs, callers must serialize direct edits to the same live graph; concurrent arbitrary token mutation is not made thread-safe by reference tracking. JsonPit's accepted Add operations retain the state/snapshot gate and identity-level synchronization. User notification handlers should observe changes, not recursively mutate the same graph or throw exceptions.

## Verification

`ReferenceTests.ArrayOperator_ReturnsAddedItem` verifies original root/nested identity and mutation visibility. `LiveReferenceTests` covers sparse fragments, historical isolation, normal and fallback timestamps, mode selection, persistence, replay, observers, protected attributes, and unreported edits. Keep identity assertions (`Assert.Same`) distinct from value assertions.
