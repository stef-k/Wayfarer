<# .SYNOPSIS
Owns a disposable source/build tree and exact host PID record, or finalizes that recorded run.
#>
[CmdletBinding()]
param([switch]$Teardown)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'shared-layout-lifecycle.ps1')
$pidFile = Join-Path ([IO.Path]::GetTempPath()) 'wayfarer-shared-layout-e2e-host.pid'

if ($Teardown) {
    if (Test-Path -LiteralPath $pidFile) {
        Assert-OrdinaryTestTree $pidFile
        $record = Get-Content -LiteralPath $pidFile -Raw | ConvertFrom-Json
        Stop-SharedLayoutHost $record
        Remove-SharedLayoutAuthority $record.OwnerPid
    }
    return
}

# Retain exclusive-port readiness without Windows-only networking cmdlets.
if ([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | Where-Object Port -eq 7150) {
    throw 'Shared-layout E2E requires exclusive use of HTTPS port 7150.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$tempRoot = Get-SharedLayoutRoot $PID
$sourceRoot = Join-Path $tempRoot 'source'
$hostOutput = Join-Path $tempRoot 'host'
$hostProcess = $null
$failure = $null
$ownsRoot = $false
# Exclusive file creation prevents concurrent launchers from replacing teardown authority.
$authority = [IO.File]::Open($pidFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
try {
    $initial = [Text.Encoding]::UTF8.GetBytes((@{ OwnerPid = $PID } | ConvertTo-Json -Compress))
    $authority.Write($initial, 0, $initial.Length)
    $authority.Flush()
    if (Test-Path -LiteralPath $tempRoot) { throw 'Launcher temp root already exists; refusing reuse.' }
    New-Item -ItemType Directory -Path $sourceRoot | Out-Null
    $ownsRoot = $true
    # tar is available on Windows and Linux; omit caches and prior test output from the copy.
    $archive = Join-Path $tempRoot 'source.tar'
    & tar --exclude=.git --exclude=bin --exclude=obj --exclude=.local --exclude=node_modules --exclude=ChromeCache --exclude=TileCache --exclude=ImageCache --exclude=Logs --exclude=Uploads --exclude=coverage-report --exclude=TestResults --exclude=playwright-report -cf $archive -C $repoRoot .
    if ($LASTEXITCODE -ne 0) { throw 'Unable to archive shared-layout sources.' }
    & tar -xf $archive -C $sourceRoot
    if ($LASTEXITCODE -ne 0) { throw 'Unable to extract shared-layout sources.' }
    Remove-Item -LiteralPath $archive
    & dotnet build (Join-Path $sourceRoot 'Wayfarer.csproj') --configuration Debug --output $hostOutput --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Unable to build shared-layout host.' }
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $hostProcess = Start-Process -FilePath dotnet -ArgumentList @("`"$(Join-Path $hostOutput 'Wayfarer.dll')`"", '--urls', 'https://127.0.0.1:7150') -WorkingDirectory $sourceRoot -PassThru
    $record = @{ OwnerPid = $PID; HostPid = $hostProcess.Id; HostStarted = $hostProcess.StartTime.ToUniversalTime().Ticks.ToString() }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($record | ConvertTo-Json -Compress))
    $authority.Position = 0
    $authority.SetLength(0)
    $authority.Write($bytes, 0, $bytes.Length)
    $authority.Dispose()
    $hostProcess.WaitForExit()
    if ($hostProcess.ExitCode -ne 0) { throw "Shared-layout host exited $($hostProcess.ExitCode)." }
} catch { $failure = $_ }
finally {
    $authority.Dispose()
    try {
        if ($hostProcess) { Stop-SharedLayoutHost $record }
        elseif ($ownsRoot) { Remove-SharedLayoutRoot $PID $tempRoot }
        Remove-SharedLayoutAuthority $PID
    } catch {
        Write-Warning "Shared-layout cleanup failed: $($_.Exception.Message)"
        if (-not $failure) { $failure = $_ }
    }
}
if ($failure) { throw $failure }
