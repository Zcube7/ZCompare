# Contributing to ZCompare

Thank you for helping improve ZCompare. The project prioritizes deterministic, read-only XLSX comparison and safe failure over broad format support.

## Before opening an issue

- Search existing issues.
- Reproduce on the latest release.
- Use a synthetic workbook whenever possible.
- Never attach confidential workbooks, comparison reports, local usernames, company paths, credentials, or internal screenshots.

## Development

Requirements: Windows 10/11 x64 and the .NET 10 SDK.

```powershell
git clone https://github.com/Zcube7/ZCompare.git
cd ZCompare
dotnet restore ZCompare.slnx
dotnet build ZCompare.slnx -c Release --no-restore
$env:WINDIR = $env:SystemRoot
dotnet test ZCompare.slnx -c Release --no-build
```

Keep changes focused. Add a regression test for every comparison semantic change. Test fixtures must be generated in temporary directories at runtime; do not commit XLSX/CSV/TSV samples. Confirm source workbook SHA-256 values are unchanged before and after comparison.

Run the public-content scan before submitting a pull request:

```powershell
./tools/Test-PublicRelease.ps1
```

By submitting a contribution, you agree that it is licensed under the Apache License 2.0.
