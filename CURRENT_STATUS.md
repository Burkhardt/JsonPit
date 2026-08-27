# JsonPit CURRENT_STATUS

Last updated: 2026-08-26

Current released line: `4.1.0`

Prepared coordinated line: `4.2.3`

## Prepared state

- The abandoned-instance watcher ownership fix remains in place: watcher callbacks and debounce work acquire the `Pit` through weak ownership.
- Forced collection can clear the registry target and make the canonical path reopenable without finalizer recovery publication, watcher disposal, or filesystem I/O.
- CR015 recursive nested tombstones, `DeletePropertyPath(...)`, and empty-parent pruning are implemented.
- Fallback package references align to `OsLibCore 4.2.3` and `RaiUtils 4.2.3`.
- `UseLocalRAIkeepSources=false` forces package references even inside the RAIkeep umbrella checkout.
- The test project references JsonPit only; it no longer bypasses package-only validation with duplicate direct OsLib/RaiUtils project references.

## Release gate

- Run the focused `SplitMasterRecoveryTests.Finalizer_PerformsNoRecoveryPublicationOrFilesystemIO_AndPathBecomesReopenable` regression.
- Run the relevant complete JsonPit Release suite, including configured cloud/remote scenarios where available.
- Do not tag or publish from this repository directly; the coordinated RAIkeep chain owns release order.

Release notes: [JsonPit_RELEASE_NOTES_4.2.3.md](https://github.com/Burkhardt/RAIkeep/blob/main/doc/JsonPit_RELEASE_NOTES_4.2.3.md)
