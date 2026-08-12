$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
dotnet run --project (Join-Path $repo 'src\ZCompare.App\ZCompare.App.csproj')
