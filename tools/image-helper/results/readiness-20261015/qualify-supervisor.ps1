# Inspect the unchanged standalone supervisor under native non-elevated Windows x64.
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.dotnet-cli'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($elevated) { throw 'The developer probe must be non-elevated.' }
dotnet --info | Out-File (Join-Path $taskRoot 'dotnet-info.log')
# Windows PowerShell 5.1 maps native startup stderr to errors even on success.
# Preserve the real native exit code while capturing both build streams.
$ErrorActionPreference = 'Continue'
dotnet build (Join-Path $taskRoot 'supervisor\ImageHelper.Supervisor.csproj') -c Release --nologo *> (Join-Path $taskRoot 'build.log')
$buildExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($buildExit -ne 0) { throw 'Native Windows supervisor Release build failed.' }
$dll = Join-Path $taskRoot 'supervisor\bin\Release\net10.0\ImageHelper.Supervisor.dll'
$output = Join-Path $taskRoot 'derivative.jpg'
# An existing trusted executable distinguishes the pre-launch platform refusal from a missing file.
$configuredExecutable = Join-Path $env:SystemRoot 'System32\whoami.exe'
$response = & dotnet $dll $configuredExecutable (Join-Path $taskRoot 'static.jpg') $output
$exitCode = $LASTEXITCODE
$response | Out-File (Join-Path $taskRoot 'response.json') -Encoding utf8
@{
    platform = 'Windows x64 native .NET'
    user = $identity.Name
    elevated = $elevated
    configuredExecutable = $configuredExecutable
    configuredExecutableExists = (Test-Path $configuredExecutable)
    supervisorExitCode = $exitCode
    outputExists = (Test-Path $output)
    response = ($response | ConvertFrom-Json)
    scope = 'Native supervisor build and pre-launch refusal only; no Windows Rust helper execution.'
} | ConvertTo-Json -Depth 5 | Out-File (Join-Path $taskRoot 'qualification.json') -Encoding utf8
Get-Content (Join-Path $taskRoot 'qualification.json')
