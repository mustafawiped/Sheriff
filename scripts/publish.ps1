[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$Output
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $projectRoot 'artifacts\publish\win-x64' }
elseif (![IO.Path]::IsPathRooted($Output)) { $Output = Join-Path $projectRoot $Output }
$publishPath = [IO.Path]::GetFullPath($Output)
Push-Location -LiteralPath $projectRoot
try {
    $null = Get-Command dotnet -ErrorAction Stop
    & dotnet publish Sheriff.App\Sheriff.App.csproj -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishReadyToRun=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false `
        -m:1 --output $publishPath
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
    $executable = Join-Path $publishPath 'Sheriff.exe'
    if (!(Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Published executable not found: $executable" }
    Write-Output "Published executable: $executable"
} finally { Pop-Location }
