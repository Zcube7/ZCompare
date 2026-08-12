# ZCompare

[简体中文](README.zh-CN.md) · [Install](docs/INSTALL.md) · [Changelog](CHANGELOG.md) · [Security](SECURITY.md)

ZCompare is a read-only Windows tool for people who maintain XLSX files across long-lived versions. It compares two workbooks or two folders, aligns inserted/deleted rows conservatively, and presents the result in synchronized spreadsheet views.

> Public preview: **v0.1.0** supports Windows 10/11 x64 and genuine OOXML `.xlsx` files only.

![ZCompare workbook comparison using synthetic data](docs/images/zcompare-workbook-diff.png)

## Why ZCompare

- **Value-first and explicit:** saved cell values are compared by default, with case sensitivity enabled. Whitespace is never trimmed.
- **No cascading red after a row insertion:** conservative alignment uses only exact row anchors. Strict original row numbers remain available.
- **Optional dimensions:** formula text, number/fill/border/alignment formatting, fonts, comments, hyperlinks, and layout can be enabled independently.
- **Useful at scale:** recursive or flat folder scans, selection independent from filtering, at most two deep comparisons in parallel, progress, cancellation, and virtualized preview grids.
- **Auditable:** JSON/XLSX reports, CLI exit codes, source-file SHA-256 validation, warnings for stale formula caches, and explicit failure for unsupported containers.
- **Read-only:** ZCompare does not edit, merge, recalculate, or save source workbooks.

## Install

Download assets only from the official [ZCompare Releases](https://github.com/Zcube7/ZCompare/releases) page.

- **Most users:** `ZCompare-0.1.0-win-x64-setup.exe`. Per-user install; no administrator rights or preinstalled .NET required.
- **Developers / advanced users:** `ZCompare-0.1.0-win-x64-portable-fdd.zip`. Requires the .NET 10 Desktop Runtime x64 and includes both GUI and CLI.
- Verify downloads with `SHA256SUMS.txt`.

The first release is not code-signed. Windows SmartScreen may show a warning. Verify the GitHub repository, filename, and SHA-256 before choosing **More info → Run anyway**. Do not disable SmartScreen.

Detailed instructions: [English](docs/INSTALL.md) · [简体中文](docs/INSTALL.zh-CN.md)

## Basic use

1. Select **File comparison** or **Folder comparison**.
2. Choose the left and right paths.
3. Keep the defaults for a saved-value, case-sensitive comparison, or enable the dimensions you need.
4. Scan a folder, mark the rows to compare, then click **Compare**.
5. Open a result to inspect aligned worksheets, navigate differences, or export JSON/XLSX.

Formula results always come from the cache stored inside the workbook; ZCompare does not run Excel or a formula engine. Missing or potentially stale caches are reported as warnings rather than silently treated as equal.

## Advanced alignment

- Conservative exact row alignment (default) reports an inserted/deleted row once without shifting every following row.
- Strict original row-number comparison restores address-to-address behavior.
- Key-column rules support a single or composite key per worksheet.
- Worksheets can be paired by name, position, or an explicit mapping.
- Explicit left/right column mappings are available; ZCompare does not guess column moves automatically.

## CLI

```powershell
zcompare --version
zcompare file left.xlsx right.xlsx --report result.json
zcompare folder left-folder right-folder --pattern "*.xlsx" --no-subdirectories --report result.xlsx
zcompare file left.xlsx right.xlsx --formulas --formatting --fonts
```

Run `zcompare --help` for worksheet pairing, key-column, column-mapping, and exit-code details.

## Build from source

Requirements: Windows 10/11 x64 and [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
git clone https://github.com/Zcube7/ZCompare.git
cd ZCompare
dotnet restore ZCompare.slnx
dotnet build ZCompare.slnx -c Release --no-restore
$env:WINDIR = $env:SystemRoot
dotnet test ZCompare.slnx -c Release --no-build
dotnet run --project src/ZCompare.App/ZCompare.App.csproj
```

## Updates and privacy

After the window loads, ZCompare performs a non-blocking update check against GitHub Releases at most once every 24 hours. If a newer stable release exists, a quiet banner opens the official installer download in your browser. ZCompare never downloads or executes an installer silently.

- No telemetry and no workbook/report upload.
- The update request sends only standard HTTP headers and the app version to GitHub.
- Recent paths and named profiles are stored locally under `%LocalAppData%\ZCompare`.
- Error dialogs stay local; v0.1.0 does not create or upload diagnostic logs.
- Source files are hashed before and after comparison and are never written by ZCompare.

See the [installation guide](docs/INSTALL.md) for the full privacy and AI-assisted installation checklist.

## Current boundaries

ZCompare does not support `.xls`, `.xlsm`, CSV, TSV, text, binary comparison, editing, merging, VBA processing, or formula recalculation. ISO Strict workbooks currently fail closed with an explicit unsupported message. Charts, images, shapes, conditional formatting, data validation, pivot tables, names, and external-link content are listed as unexamined objects when the relevant option is enabled.

Performance varies by hardware and workbook structure. On an anonymized maintainer benchmark of roughly 1.1 million non-empty cells per side across dozens of worksheets, common comparison modes completed in approximately 6–13 seconds with peak working set below 0.5 GB. This is an indicative range, not a guarantee.

## AI assistance and third-party software

OpenAI Codex and Anthropic Claude assisted with requirements analysis, coding, tests, review, and documentation. The project owner remains responsible for all product decisions, releases, and maintenance. Contributions must still be reviewed and tested like any other code.

ZCompare uses the Open XML SDK. OfficeCLI influenced a narrow portion of display-format and theme-color behavior but is not a build or runtime dependency. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## License

Apache License 2.0. See [LICENSE](LICENSE).
