param([string]$Exe = (Join-Path $PSScriptRoot '..\Sheriff.App\bin\Debug\net8.0-windows\win-x64\Sheriff.exe'))
$ErrorActionPreference = 'Stop'
$exePath = (Resolve-Path -LiteralPath $Exe).Path
$testRoot = Join-Path $PSScriptRoot '..\artifacts\single-instance'
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$log = Join-Path $testRoot 'logs\diagnostics.log'
if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log }
function Start-Sheriff([string]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($exePath, $Arguments)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.Environment['SHERIFF_DATA_DIR'] = [IO.Path]::GetFullPath($testRoot)
    $info.Environment['SHERIFF_TEST_INSTANCE'] = 'verification'
    return [Diagnostics.Process]::Start($info)
}
$primary = Start-Sheriff '--background'
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 150; $attempt++) {
        if ((Test-Path -LiteralPath $log) -and ((Get-Content -LiteralPath $log -Raw) -match 'READY hwnd=')) { $ready = $true; break }
        if ($primary.HasExited) { throw "Primary exited with $($primary.ExitCode)" }
        Start-Sleep -Milliseconds 100
    }
    if (!$ready) { throw 'Primary did not become ready within 15 seconds' }
    $timings = @()
    foreach ($index in 1..3) {
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $secondary = Start-Sheriff ''
        if (!$secondary.WaitForExit(5000)) { throw 'Second instance stayed alive' }
        $timings += [Math]::Round($clock.Elapsed.TotalMilliseconds, 2)
        if ($secondary.ExitCode -ne 0) { throw "Second instance failed: $($secondary.ExitCode)" }
        if ($primary.HasExited) { throw 'Primary terminated during toggle' }
        Start-Sleep -Milliseconds 250
    }
    $exitProcess = Start-Sheriff '--exit'
    if (!$exitProcess.WaitForExit(5000) -or !$primary.WaitForExit(5000)) { throw 'Pipe exit failed' }
    $contents = Get-Content -LiteralPath $log -Raw
    $readyCount = ([regex]::Matches($contents, 'READY hwnd=')).Count
    $shows = [regex]::Matches($contents, 'SHOW_RETURN [\d.,]+ ms hwnd=(\d+)')
    $handles = @($shows | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    if ($readyCount -ne 1 -or $shows.Count -ne 2 -or $handles.Count -ne 1) { throw "Unexpected window lifecycle: ready=$readyCount shows=$($shows.Count) HWNDs=$($handles.Count)" }
    [pscustomobject]@{ Result = 'PASS'; PrimaryPid = $primary.Id; Hwnd = $handles[0]; ToggleCount = 3; SecondaryProcessMs = $timings; Log = [IO.Path]::GetFullPath($log) } | ConvertTo-Json
} finally {
    if (!$primary.HasExited) { $cleanup = Start-Sheriff '--exit'; $null = $cleanup.WaitForExit(5000) }
}
