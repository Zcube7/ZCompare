[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$InnoCompiler,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$releaseDirectory = Join-Path $artifactsRoot 'release'
$stagingDirectory = Join-Path $artifactsRoot 'release-staging'

function Reset-SafeDirectory([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $allowedPrefix = $artifactsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a directory outside artifacts: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolved | Out-Null
}

[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
$version = [string]$buildProperties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Directory.Build.props contains an invalid Version: $version"
}
if ($env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME -ne "v$version") {
    throw "Tag $($env:GITHUB_REF_NAME) does not match Version $version."
}

Reset-SafeDirectory $releaseDirectory
Reset-SafeDirectory $stagingDirectory

$portableDirectory = Join-Path $stagingDirectory 'portable'
$installerPayload = Join-Path $stagingDirectory 'installer-payload'
New-Item -ItemType Directory -Path $portableDirectory, $installerPayload | Out-Null

$appProject = Join-Path $repoRoot 'src\ZCompare.App\ZCompare.App.csproj'
$cliProject = Join-Path $repoRoot 'src\ZCompare.Cli\ZCompare.Cli.csproj'
dotnet restore $appProject -r $Runtime
if ($LASTEXITCODE -ne 0) { throw 'Runtime-specific app restore failed.' }
dotnet restore $cliProject -r $Runtime
if ($LASTEXITCODE -ne 0) { throw 'Runtime-specific CLI restore failed.' }

dotnet publish $appProject --no-restore `
    -c $Configuration -r $Runtime --self-contained false `
    -p:DebugType=None -p:DebugSymbols=false -o $portableDirectory
if ($LASTEXITCODE -ne 0) { throw 'Framework-dependent app publish failed.' }

dotnet publish $cliProject --no-restore `
    -c $Configuration -r $Runtime --self-contained false `
    -p:DebugType=None -p:DebugSymbols=false -o $portableDirectory
if ($LASTEXITCODE -ne 0) { throw 'Framework-dependent CLI publish failed.' }

dotnet publish $appProject --no-restore `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false `
    -p:DebugType=None -p:DebugSymbols=false -o $installerPayload
if ($LASTEXITCODE -ne 0) { throw 'Self-contained app publish failed.' }

Get-ChildItem -LiteralPath $stagingDirectory -Recurse -File |
    Where-Object Extension -eq '.pdb' |
    Remove-Item -Force
$portableDocs = Join-Path $portableDirectory 'docs'
New-Item -ItemType Directory -Path $portableDocs | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $portableDocs
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') -Destination $portableDocs
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $portableDocs
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.zh-CN.md') -Destination $portableDocs

$portableArchive = Join-Path $releaseDirectory "ZCompare-$version-win-x64-portable-fdd.zip"
Compress-Archive -Path (Join-Path $portableDirectory '*') -DestinationPath $portableArchive -CompressionLevel Optimal

if (-not $SkipInstaller) {
    if ([string]::IsNullOrWhiteSpace($InnoCompiler)) {
        $innoCandidates = @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
        )
        $InnoCompiler = $innoCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
    if ([string]::IsNullOrWhiteSpace($InnoCompiler) -or -not (Test-Path -LiteralPath $InnoCompiler)) {
        throw 'Inno Setup 6 was not found. Install it or pass -InnoCompiler; use -SkipInstaller only for local portable builds.'
    }
    & $InnoCompiler "/DMyAppVersion=$version" "/DSourceDir=$installerPayload" "/DOutputDir=$releaseDirectory" (Join-Path $repoRoot 'installer\ZCompare.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
}

$releaseFiles = Get-ChildItem -LiteralPath $releaseDirectory -File |
    Where-Object Name -ne 'SHA256SUMS.txt' |
    Sort-Object Name
$hashLines = foreach ($file in $releaseFiles) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($file.Name)"
}
Set-Content -LiteralPath (Join-Path $releaseDirectory 'SHA256SUMS.txt') -Value $hashLines -Encoding ascii

& (Join-Path $repoRoot 'tools\Test-ReleaseAssets.ps1') -ReleaseDirectory $releaseDirectory -Version $version -InstallerOptional:$SkipInstaller
if ($LASTEXITCODE -ne 0) { throw 'Release asset validation failed.' }

Write-Host "Release assets created in $releaseDirectory"
