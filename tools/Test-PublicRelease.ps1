[CmdletBinding()]
param([switch]$IncludeHistory)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$excludedDirectories = @('.git', 'bin', 'obj', 'artifacts', 'TestResults', '.vs')
$textExtensions = @('.cs', '.xaml', '.xml', '.json', '.md', '.ps1', '.iss', '.yml', '.yaml', '.props', '.csproj', '.slnx', '.txt')
$patterns = [ordered]@{
    'company identity' = '(?i)NIBIRUTECH|@nibirutech\.com'
    'local user path' = '(?i)C:[\\/]Users[\\/]zhaozhenzhen'
    'internal workspace' = '(?i)BA_config|(?:^|[^a-z0-9])u110(?:50|60|70)(?:[^a-z0-9]|$)'
    'private workbook name' = '(?i)c_Common_text\.xlsx|s_server_localization\.xlsx'
    'credential material' = '(?i)github_pat_[A-Za-z0-9_]+|ghp_[A-Za-z0-9]+|AKIA[0-9A-Z]{16}|BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY'
}

$violations = [Collections.Generic.List[string]]::new()
function Get-RepoRelativePath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside repository: $fullPath"
    }
    return $fullPath.Substring($repoPrefix.Length)
}

$files = Get-ChildItem -LiteralPath $repoRoot -Recurse -File | Where-Object {
    $relative = Get-RepoRelativePath $_.FullName
    $segments = $relative -split '[\\/]'
    $relative -ne 'tools\Test-PublicRelease.ps1' -and
    -not ($segments | Where-Object { $excludedDirectories -contains $_ }) -and
    $textExtensions -contains $_.Extension
}
foreach ($file in $files) {
    $relative = Get-RepoRelativePath $file.FullName
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $file.FullName -Encoding utf8) {
        $lineNumber++
        foreach ($entry in $patterns.GetEnumerator()) {
            if ($line -match $entry.Value) {
                $violations.Add("$relative`:$lineNumber [$($entry.Key)]")
            }
        }
    }
}

$dataFiles = Get-ChildItem -LiteralPath $repoRoot -Recurse -File | Where-Object {
    $relative = Get-RepoRelativePath $_.FullName
    $segments = $relative -split '[\\/]'
    -not ($segments | Where-Object { $excludedDirectories -contains $_ }) -and
    $_.Extension -match '(?i)^\.(xlsx|xlsm?|csv|tsv)$'
}
foreach ($file in $dataFiles) {
    $violations.Add("$(Get-RepoRelativePath $file.FullName) [data file]")
}

if ($IncludeHistory) {
    $history = git -C $repoRoot log -p --all -- . ':(exclude)tools/Test-PublicRelease.ps1' 2>$null | Out-String
    foreach ($entry in $patterns.GetEnumerator()) {
        if ($history -match $entry.Value) {
            $violations.Add("Git history [$($entry.Key)]")
        }
    }
}

if ($violations.Count -gt 0) {
    $violations | Sort-Object -Unique | ForEach-Object { Write-Error $_ }
    throw "Public release scan found $($violations.Count) potential disclosure(s)."
}

Write-Host 'Public release scan passed.'
