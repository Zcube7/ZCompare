[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$ReleaseDirectory,
    [Parameter(Mandatory)] [string]$Version,
    [switch]$InstallerOptional
)

$ErrorActionPreference = 'Stop'
$releaseDirectory = [IO.Path]::GetFullPath($ReleaseDirectory)
$portableName = "ZCompare-$Version-win-x64-portable-fdd.zip"
$setupName = "ZCompare-$Version-win-x64-setup.exe"
$checksumName = 'SHA256SUMS.txt'
$required = @($portableName, $checksumName)
if (-not $InstallerOptional) { $required += $setupName }

foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $releaseDirectory $name) -PathType Leaf)) {
        throw "Missing release asset: $name"
    }
}

$portable = Get-Item -LiteralPath (Join-Path $releaseDirectory $portableName)
if ($portable.Length -gt 12MB) { throw "Portable archive exceeds 12 MB: $($portable.Length) bytes" }
if (-not $InstallerOptional) {
    $setup = Get-Item -LiteralPath (Join-Path $releaseDirectory $setupName)
    if ($setup.Length -gt 70MB) { throw "Installer exceeds 70 MB: $($setup.Length) bytes" }
}

$expectedHashes = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $releaseDirectory $checksumName)) {
    if ($line -notmatch '^([0-9a-fA-F]{64})  (.+)$') { throw "Invalid SHA256SUMS line: $line" }
    $expectedHashes[$Matches[2]] = $Matches[1].ToLowerInvariant()
}
foreach ($name in $required | Where-Object { $_ -ne $checksumName }) {
    $actual = (Get-FileHash -LiteralPath (Join-Path $releaseDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expectedHashes[$name] -ne $actual) { throw "Checksum mismatch for $name" }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($portable.FullName)
try {
    $entryNames = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    foreach ($requiredEntry in @('ZCompare.App.exe', 'zcompare.exe', 'docs/LICENSE', 'docs/README.md', 'docs/README.zh-CN.md')) {
        if ($entryNames -notcontains $requiredEntry) { throw "Portable archive is missing $requiredEntry" }
    }
    $forbidden = $entryNames | Where-Object {
        $_ -match '(?i)(\.pdb$|(^|/)tests?(/|$)|(^|/)(bin|obj)(/|$)|officecli|\.(xlsx|xlsm?|csv|tsv)$)'
    }
    if ($forbidden) { throw "Portable archive contains forbidden entries: $($forbidden -join ', ')" }
}
finally {
    $archive.Dispose()
}

Write-Host "Release assets validated for ZCompare $Version."
