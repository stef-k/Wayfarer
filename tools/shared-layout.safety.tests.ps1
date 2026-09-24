$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'shared-layout-lifecycle.ps1')
$root = Get-SharedLayoutRoot $PID
if (Test-Path $root) { throw 'Safety test refuses an existing owned root.' }
try {
    foreach ($invalid in @([IO.Path]::GetTempPath(), "$root-other", (Join-Path $root 'child'))) {
        $rejected = $false
        try { Remove-SharedLayoutRoot $PID $invalid } catch { $rejected = $true }
        if (!$rejected) { throw "Accepted non-owned path: $invalid" }
    }
    New-Item -ItemType Directory -Path $root | Out-Null
    Set-Content (Join-Path $root 'sentinel') 'owned'
    Remove-SharedLayoutRoot $PID $root
    Remove-SharedLayoutRoot $PID $root
    if (Test-Path $root) { throw 'Owned tree remains.' }
    # Use an owned sleeper to prove stop-before-delete without a browser/app build.
    $sleeper = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep -Seconds 30') -PassThru
    try {
        $record = @{ OwnerPid = $PID; HostPid = $sleeper.Id; HostStarted = 'wrong-start-time' }
        $rejected = $false
        try { Stop-SharedLayoutHost $record } catch { $rejected = $true }
        if (!$rejected -or $sleeper.HasExited) { throw 'Mismatched host identity was not preserved.' }
        New-Item -ItemType Directory -Path $root | Out-Null
        $record.HostStarted = $sleeper.StartTime.ToUniversalTime().Ticks.ToString()
        Stop-SharedLayoutHost $record
        if (!$sleeper.WaitForExit(10000) -or (Test-Path $root)) { throw 'Host/tree cleanup failed.' }
        Stop-SharedLayoutHost $record
    } finally {
        if (!$sleeper.HasExited) { $sleeper.Kill(); $sleeper.WaitForExit() }
    }
    Write-Host 'PASS: non-owned paths and reused PIDs rejected; exact host stopped and tree removed idempotently.' 
} finally {
    Remove-SharedLayoutRoot $PID $root
}
