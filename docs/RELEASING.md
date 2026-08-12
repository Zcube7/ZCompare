# Release process

This document is for maintainers. A release must be reproducible from a clean public commit and must never use private workbook data.

## Prerequisites

- Windows x64
- .NET 10 SDK matching `global.json`
- Inno Setup 6
- GitHub CLI authenticated as a maintainer

## Prepare

1. Update `Version` in `Directory.Build.props` and add the changelog entry.
2. Confirm all screenshots and fixtures use synthetic data.
3. Run:

   ```powershell
   dotnet restore ZCompare.slnx
   dotnet build ZCompare.slnx -c Release --no-restore --warnaserror
   $env:WINDIR = $env:SystemRoot
   dotnet test ZCompare.slnx -c Release --no-build
   ./tools/Test-PublicRelease.ps1 -IncludeHistory
   ./tools/build-release.ps1
   ```

4. Inspect `artifacts/release`. It must contain the setup executable, portable ZIP, and `SHA256SUMS.txt` only. Verify package sizes and hashes.

## Create the draft

```powershell
git tag -a vX.Y.Z -m "ZCompare vX.Y.Z"
git push origin vX.Y.Z
```

The tag workflow rebuilds and retests the exact commit, creates checksums and build provenance, and opens a Draft Release. Draft and prerelease versions are intentionally invisible to the application's `releases/latest` update check.

## Verify before publishing

- Download every asset from the Draft Release rather than testing only local output.
- Recompute SHA-256 and compare with `SHA256SUMS.txt`.
- Test first install, launch, repair/reinstall, in-place upgrade, uninstall, and local-profile retention on a clean Windows x64 environment.
- Compare two synthetic workbooks and two synthetic folders; verify source hashes do not change.
- Check file metadata, icon, license, notices, English/Chinese docs, SmartScreen wording, and package contents.
- Confirm a current-version update response shows no banner and a controlled higher-version response shows the official installer URL.
- Scan Release assets, screenshots, workflow logs, and reachable Git history for private data.

Only then publish the Draft Release. Do not move or recreate the tag after publication.
