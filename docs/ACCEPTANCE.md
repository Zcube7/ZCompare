# Acceptance checklist

Use synthetic workbooks generated at runtime. Never add real user or company data to the repository, build output, screenshots, or CI logs.

## Comparison correctness

- Value kinds remain distinct: exact decimal number, text, Boolean, error, date/time, blank, and formula cache.
- Text preserves case according to the option and always preserves spaces, tabs, carriage returns, and line feeds.
- Formula text and cached results are reported separately; missing or potentially stale cache values produce a warning.
- Conservative row alignment reports isolated inserted/deleted rows without cascading later differences; strict mode compares original addresses.
- Formatting, font, comment, hyperlink, and layout options remain independent and do not alter row identity.
- Unsupported or malformed containers fail closed and keep a locatable error reason.
- Source SHA-256 values are identical before and after every comparison.

## Folder and UI

- Flat folder scanning is the default; recursion requires explicit selection.
- Pairing is case-insensitive by relative path, temporary files are ignored, and directory links are not followed.
- Marks remain unchanged while searching or switching between all/difference filters.
- Deep comparison never exceeds two workbook pairs in parallel; progress remains monotonic and cancellation is responsive.
- Side-by-side preview remains virtualized, synchronized, and stable while switching worksheets rapidly.
- Inserted/deleted rows show placeholders on the missing side; changed rows and exact cells use distinct highlighting.
- Warning and error reasons are reachable from the folder result list.

## Release

- Release build has zero warnings and every automated test passes.
- Setup is at most 70 MB; portable ZIP is at most 12 MB.
- Packages contain no PDB, tests, workbook data, build caches, or OfficeCLI executable.
- Installer is per-user, uses the fixed AppId, upgrades in place, and preserves local configuration on uninstall.
- Update checks are non-blocking and silent on 304, 404, rate limit, timeout, malformed JSON, and missing installer assets.
- Public files, complete reachable history, screenshots, Release assets, and Actions logs contain no private paths, identities, workbook names, or credentials.
