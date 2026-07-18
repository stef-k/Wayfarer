<#!
.SYNOPSIS
Builds a disposable copy outside the repository and foregrounds its isolated HTTPS host for Playwright.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$port = 7150
$repoRoot = Split-Path -Parent $PSScriptRoot
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "wayfarer-shared-layout-e2e-$PID"
$sourceRoot = Join-Path $tempRoot 'source'
$hostOutput = Join-Path $tempRoot 'host'
$pidFile = Join-Path ([System.IO.Path]::GetTempPath()) 'wayfarer-shared-layout-e2e-host.pid'

if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) {
    throw "Shared-layout E2E requires exclusive use of HTTPS port $port."
}

New-Item -ItemType Directory -Path $sourceRoot -Force | Out-Null
& robocopy $repoRoot $sourceRoot /E /XD .git bin obj .local node_modules /NFL /NDL /NJH /NJS /NC /NS
if ($LASTEXITCODE -gt 1) {
    throw "Unable to prepare the isolated shared-layout E2E source copy (robocopy exit code $LASTEXITCODE)."
}

# Build artifacts and SDK intermediates stay in the disposable TEMP copy, never under the repository.
& dotnet build (Join-Path $sourceRoot 'Wayfarer.csproj') --configuration Debug --output $hostOutput --nologo
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to build the isolated shared-layout E2E host.'
}

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$hostProcess = Start-Process -FilePath dotnet -ArgumentList @((Join-Path $hostOutput 'Wayfarer.dll'), '--urls', "https://127.0.0.1:$port") -WorkingDirectory $sourceRoot -WindowStyle Hidden -PassThru
$hostProcess.Id | Set-Content -LiteralPath $pidFile -NoNewline

# Wait in the foreground; global teardown verifies this exact listener is stopped after the suite.
$hostProcess.WaitForExit()
Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
exit $hostProcess.ExitCode
