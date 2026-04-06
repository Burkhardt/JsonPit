# JsonPit 3.5.0 Release Notes

## Highlights

- Version bumped to `3.5.0`.
- JsonPit now documents the supported cloud-backed provider claim as `OneDrive`, `GoogleDrive`, and `Dropbox`.
- `PitItem.Id` replaces `Name` as the canonical framework identifier.
- JsonPit package metadata and fallback package references were aligned with the shared `3.5.0` package line.

## Compatibility

- Legacy persisted payloads that still contain `Name` without `Id` are normalized internally to `Id`.
- During normalization, `Name` is copied into `Id` when `Id` is missing, and the original `Name` value is preserved.
- Future use of `Name` as an application-defined custom field remains supported.

## Documentation

- `README.md`, `GettingStarted.md`, and `Requirements.md` were updated for the `3.5.0` package line.
- The package installation examples now reference `3.5.0`.
- The cloud-storage guidance now reflects the supported provider claim used across the `RAIkeep` package stack.
- JsonPit guidance now references `Os.CloudStorageRootDir`, `Os.UserHomeDir`, and the current `RaiPath`-based OsLib path helpers.

## Validation

- JsonPit remained green in the full workspace validation run for `3.5.0`.