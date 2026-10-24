[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$Output,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $projectRoot 'artifacts\test-results' }
elseif (![IO.Path]::IsPathRooted($Output)) { $Output = Join-Path $projectRoot $Output }
$resultPath = [IO.Path]::GetFullPath($Output)
function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed (exit $LASTEXITCODE)." }
}
Push-Location -LiteralPath $projectRoot
try {
    $null = Get-Command dotnet -ErrorAction Stop
    Invoke-Dotnet -Arguments @('restore', 'Sheriff.sln', '-m:1')
    Invoke-Dotnet -Arguments @('build', 'Sheriff.sln', '-c', $Configuration, '--no-restore', '-m:1')
    if (!$SkipTests) {
        Invoke-Dotnet -Arguments @('test', 'Sheriff.Tests\Sheriff.Tests.csproj', '-c', $Configuration,
            '--no-build', '--no-restore', '-m:1', '--logger', 'trx;LogFileName=tests.trx', '--results-directory', $resultPath)
    }
    Write-Output "Build complete: $Configuration"
    if (!$SkipTests) { Write-Output "Test results: $resultPath" }
} finally { Pop-Location }
