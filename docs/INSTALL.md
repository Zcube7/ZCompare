# Installing ZCompare

[简体中文](INSTALL.zh-CN.md)

## Option A: installer (recommended)

1. Open the official [ZCompare Releases](https://github.com/Zcube7/ZCompare/releases) page.
2. Download `ZCompare-0.1.1-win-x64-setup.exe` and `SHA256SUMS.txt`.
3. Verify the SHA-256 value:

   ```powershell
   Get-FileHash .\ZCompare-0.1.1-win-x64-setup.exe -Algorithm SHA256
   Get-Content .\SHA256SUMS.txt
   ```

4. Run the installer. It installs for the current user under `%LocalAppData%\Programs\ZCompare` and does not require administrator rights or a separate .NET installation.

The installer is not code-signed in v0.1.1. SmartScreen may warn about an unrecognized app. Continue through **More info → Run anyway** only after verifying the official source and checksum. Never disable SmartScreen for installation.

Future installers use the same AppId and upgrade in place. Uninstalling the app does not remove recent comparisons and profiles from `%LocalAppData%\ZCompare`.

## Option B: portable developer package

1. Install the [.NET 10 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Download and verify `ZCompare-0.1.1-win-x64-portable-fdd.zip`.
3. Extract the archive into a new folder.
4. Run `ZCompare.App.exe`, or open a terminal in that folder and run `zcompare.exe --help`.

Do not run the application directly from inside the ZIP archive.

## Option C: build from source

Install Git and the .NET 10 SDK, then:

```powershell
git clone https://github.com/Zcube7/ZCompare.git
cd ZCompare
dotnet restore ZCompare.slnx
dotnet build ZCompare.slnx -c Release --no-restore
$env:WINDIR = $env:SystemRoot
dotnet test ZCompare.slnx -c Release --no-build
dotnet run --project src/ZCompare.App/ZCompare.App.csproj
```

## Option D: AI-assisted installation

You may give the following prompt to a local AI assistant. Read and approve every command before it runs.

> Install ZCompare for Windows x64. Download only from the official `Zcube7/ZCompare` GitHub Release. Download `SHA256SUMS.txt`, verify the selected asset with SHA-256, and show me the result before running anything. Do not bypass or disable Windows security. Do not run an installer until I explicitly approve it. Do not upload or reveal any XLSX file, local path, comparison report, configuration, username, or company data.

## Updating

ZCompare checks the latest stable GitHub Release in the background at most once every 24 hours. A new-version banner opens the official installer asset in your browser. Verify the new checksum, close ZCompare, then run the installer to upgrade. ZCompare never silently downloads or executes updates.

## Network and privacy

The application does not send workbooks, paths, reports, or settings to a server. The optional update check contacts `api.github.com` and sends only standard HTTP headers plus the installed version. Recent paths and update-cache metadata stay under `%LocalAppData%\ZCompare`. No diagnostic log file is created in v0.1.1.
