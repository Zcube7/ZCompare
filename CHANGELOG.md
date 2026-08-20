# Changelog

All notable changes to ZCompare are documented here. The project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.2] - 2026-08-20

### Added

- Added complete raw/display-value viewing with horizontal scrolling for long cell contents.
- Highlighted changed character spans in both the worksheet grid and cell-detail window while keeping copied text unmodified.

### Fixed

- Fixed a crash when double-clicking highlighted text inside a differing cell.
- Removed synthetic `⟦ ⟧` difference markers from cell-detail text and preserved real brackets, whitespace, and trailing spaces when copying.

## [0.1.1] - 2026-08-12

### Fixed

- Corrected the application, installer, shortcut, and release branding to use the approved ZCompare icon geometry.

## [0.1.0] - 2026-08-12

### Added

- Read-only semantic comparison for two XLSX files or two folders.
- Conservative row alignment, strict row-number mode, key columns, worksheet pairing, and explicit column mapping.
- Side-by-side virtualized worksheet preview with value and optional formula, formatting, font, comment, hyperlink, and layout differences.
- JSON/XLSX reports, command-line interface, named local profiles, and recent comparisons.
- Safe handling for stale formula caches, changed source files, unsupported ISO Strict files, and unreadable containers.
- Per-user Windows installer, portable developer package, release checksums, and non-intrusive update notification.

[Unreleased]: https://github.com/Zcube7/ZCompare/compare/v0.1.2...HEAD
[0.1.2]: https://github.com/Zcube7/ZCompare/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/Zcube7/ZCompare/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/Zcube7/ZCompare/releases/tag/v0.1.0
