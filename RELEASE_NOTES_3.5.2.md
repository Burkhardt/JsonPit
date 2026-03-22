# JsonPit 3.5.2 Release Notes

## Highlights

- Version bumped to `3.5.2`.
- JsonPit now aligns with the shared `3.5.2` package line across `RAIkeep`.
- Fallback package references now align with `OsLibCore 3.5.2` and `RaiUtils 3.5.2`.
- The fallback utility package id is now `RaiUtils` rather than `RaiUtilsCore`.

## Compatibility

- Legacy persisted payloads that still contain `Name` without `Id` are normalized internally to `Id`.
- During normalization, the framework-managed `Name` field is dropped.
- Future use of `Name` as an application-defined custom field remains supported.

## Documentation

- `README.md`, `GettingStarted.md`, and `Requirements.md` were updated for the `3.5.2` package line.
- Package installation examples now reference `JsonPit 3.5.2`, `RaiUtils 3.5.2`, and `OsLibCore 3.5.2`.
- JsonPit guidance now uses `Os.CloudStorageRootDir` in its path-composition examples.

## Validation

- JsonPit should be validated with the local `JsonPit` test suite and package build before publishing.