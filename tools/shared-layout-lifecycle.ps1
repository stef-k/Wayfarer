. (Join-Path $PSScriptRoot 'test-artifact-paths.ps1')

# Derives the exact launcher-owned root from the actual OS temp directory and numeric PID.
function Get-SharedLayoutRoot {
    param([int]$OwnerPid)
    if ($OwnerPid -le 0) { throw 'Expected a positive launcher PID.' }
    return Join-Path ([IO.Path]::GetTempPath()) "wayfarer-shared-layout-e2e-$OwnerPid"
}

# Rejects caller-selected paths; either lifecycle side may remove the same exact root first.
function Remove-SharedLayoutRoot {
    param([int]$OwnerPid, [string]$Path)
    $expected = Get-SharedLayoutRoot $OwnerPid
    $comparison = if ($env:OS -eq 'Windows_NT') { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not [IO.Path]::GetFullPath($Path).Equals([IO.Path]::GetFullPath($expected), $comparison)) {
        throw 'Refusing a non-owned shared-layout path.'
    }
    Remove-OwnedTestDirectory -Root ([IO.Path]::GetTempPath()) -Name "wayfarer-shared-layout-e2e-$OwnerPid"
}

# The exclusive PID file records both process identities; start time guards against PID reuse.
function Stop-SharedLayoutHost {
    param($Record)
    if ([int]$Record.OwnerPid -le 0 -or [int]$Record.HostPid -le 0 -or -not $Record.HostStarted) {
        throw 'Invalid shared-layout PID record.'
    }
    $hostProcess = Get-Process -Id $Record.HostPid -ErrorAction SilentlyContinue
    if ($hostProcess) {
        if ($hostProcess.StartTime.ToUniversalTime().Ticks.ToString() -cne [string]$Record.HostStarted) {
            throw 'Shared-layout host PID was reused; preserving residue.'
        }
        Stop-Process -Id $hostProcess.Id -Force -ErrorAction Stop
        if (-not $hostProcess.WaitForExit(10000)) { throw 'Shared-layout host did not stop.' }
    }
    Remove-SharedLayoutRoot -OwnerPid $Record.OwnerPid -Path (Get-SharedLayoutRoot $Record.OwnerPid)
}

# A competing lifecycle side may already have removed the record; never remove another run's file.
function Remove-SharedLayoutAuthority {
    param([int]$OwnerPid)
    $file = Join-Path ([IO.Path]::GetTempPath()) 'wayfarer-shared-layout-e2e-host.pid'
    if (-not (Test-Path -LiteralPath $file)) { return }
    Assert-OrdinaryTestTree $file
    $current = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    if ([int]$current.OwnerPid -eq $OwnerPid) {
        Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
    }
}
