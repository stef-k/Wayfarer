param()

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$runId = [Guid]::NewGuid().ToString('N')
$runRoot = Join-Path $repository ".local\407-waypoint-$runId"
$databaseDirectory = Join-Path $runRoot 'postgres'
$publishDirectory = Join-Path $runRoot 'publish'
$helperDirectory = Join-Path $runRoot 'fixture'
$browserDirectory = Join-Path $runRoot 'chromium'
$manifestPath = Join-Path $runRoot 'fixture.json'
$databaseLog = Join-Path $runRoot 'postgres.log'
$hostLog = Join-Path $runRoot 'host.log'
$hostErrorLog = Join-Path $runRoot 'host-error.log'
$postgresBin = 'C:\Program Files\PostgreSQL\17\bin'
$databaseName = 'wayfarer_import_tests'
$hostProcess = $null
$databaseStarted = $false
$originalFailure = $null
$cleanupFailures = [System.Collections.Generic.List[Exception]]::new()
$ownedVariables = @(
    'WAYFARER_TEST_POSTGRES_CONNECTION', 'ConnectionStrings__DefaultConnection', 'ASPNETCORE_ENVIRONMENT',
    'ASPNETCORE_URLS', 'WAYFARER_E2E_BASE_URL', 'WAYFARER_E2E_USERNAME', 'WAYFARER_E2E_PASSWORD',
    'WAYFARER_E2E_TRIP_ID', 'WAYFARER_E2E_WAYPOINT_FIXTURE', 'WAYFARER_E2E_WAYPOINT_HELPER',
    'PLAYWRIGHT_BROWSERS_PATH', 'Logging__LogFilePath__Default')
$originalVariables = @{}
foreach ($name in $ownedVariables) { $originalVariables[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port } finally { $listener.Stop() }
}

function Test-Port([int]$port) {
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $task = $client.ConnectAsync('127.0.0.1', $port)
        return $task.Wait(500) -and $client.Connected
    } catch { return $false } finally { $client.Dispose() }
}

function Wait-Port([int]$port, [bool]$open) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        if ((Test-Port $port) -eq $open) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Port $port did not become $(if ($open) { 'open' } else { 'free' })."
}

function Invoke-Checked([string]$file, [string[]]$arguments) {
    & $file @arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code ${LASTEXITCODE}: $file $($arguments -join ' ')" }
}

try {
    New-Item -ItemType Directory -Path $runRoot | Out-Null
    $databasePort = Get-FreePort
    do { $hostPort = Get-FreePort } while ($hostPort -eq $databasePort)
    $connection = "Host=127.0.0.1;Port=$databasePort;Database=$databaseName;Username=postgres"
    $password = "Issue407!$runId"

    Invoke-Checked (Join-Path $postgresBin 'initdb.exe') @('-D', $databaseDirectory, '-U', 'postgres', '-A', 'trust', '--no-locale', '--encoding=UTF8')
    Invoke-Checked (Join-Path $postgresBin 'pg_ctl.exe') @('-D', $databaseDirectory, '-l', $databaseLog, '-o', "-p $databasePort", 'start')
    $databaseStarted = $true
    Wait-Port $databasePort $true
    Invoke-Checked (Join-Path $postgresBin 'createdb.exe') @('-h', '127.0.0.1', '-p', "$databasePort", '-U', 'postgres', $databaseName)
    Invoke-Checked (Join-Path $postgresBin 'psql.exe') @('-h', '127.0.0.1', '-p', "$databasePort", '-U', 'postgres', '-d', $databaseName, '-c', 'CREATE EXTENSION IF NOT EXISTS postgis;')

    $env:WAYFARER_TEST_POSTGRES_CONNECTION = $connection
    $env:ConnectionStrings__DefaultConnection = $connection
    $env:PLAYWRIGHT_BROWSERS_PATH = $browserDirectory
    $env:Logging__LogFilePath__Default = (Join-Path $runRoot 'wayfarer-.log')
    Push-Location $repository
    try {
        Invoke-Checked 'npm.cmd' @('run', 'build')
        Invoke-Checked 'dotnet.exe' @('publish', 'Wayfarer.csproj', '-c', 'Release', '-o', $publishDirectory)
        Invoke-Checked 'dotnet.exe' @('publish', 'tools\Wayfarer.WaypointBrowserFixture\Wayfarer.WaypointBrowserFixture.csproj', '-c', 'Release', '-o', $helperDirectory)
        Invoke-Checked 'npx.cmd' @('playwright', 'install', 'chromium')
        $helper = Join-Path $helperDirectory 'Wayfarer.WaypointBrowserFixture.dll'
        Invoke-Checked 'dotnet.exe' @($helper, 'provision', $manifestPath, $password)
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

        $env:ASPNETCORE_ENVIRONMENT = 'Production'
        $env:ASPNETCORE_URLS = "http://127.0.0.1:$hostPort"
        $env:WAYFARER_E2E_BASE_URL = $env:ASPNETCORE_URLS
        $env:WAYFARER_E2E_USERNAME = [string]$manifest.username
        $env:WAYFARER_E2E_PASSWORD = $password
        $env:WAYFARER_E2E_TRIP_ID = [string]$manifest.tripId
        $env:WAYFARER_E2E_WAYPOINT_FIXTURE = $manifestPath
        $env:WAYFARER_E2E_WAYPOINT_HELPER = $helper
        $hostProcess = Start-Process -FilePath 'dotnet.exe' -ArgumentList (Join-Path $publishDirectory 'Wayfarer.dll') -WorkingDirectory $publishDirectory -PassThru -WindowStyle Hidden -RedirectStandardOutput $hostLog -RedirectStandardError $hostErrorLog
        Wait-Port $hostPort $true
        Invoke-Checked 'npx.cmd' @('playwright', 'test', 'tests/e2e/trip-editor/tripEditorWaypointAggregateContracts.spec.ts', '--config=playwright.config.ts', '--project=chromium', '--workers=1', '--retries=0', '--reporter=line')
    } finally {
        Pop-Location
    }
} catch {
    $originalFailure = $_.Exception
} finally {
    if ($hostProcess -and !$hostProcess.HasExited) {
        try { Stop-Process -Id $hostProcess.Id -ErrorAction Stop; $hostProcess.WaitForExit(10000) | Out-Null } catch { $cleanupFailures.Add($_.Exception) }
    }
    if ($hostPort) { try { Wait-Port $hostPort $false } catch { $cleanupFailures.Add($_.Exception) } }
    if (Test-Path -LiteralPath $manifestPath) {
        try {
            if (!(Test-Port $databasePort)) {
                Invoke-Checked (Join-Path $postgresBin 'pg_ctl.exe') @('-D', $databaseDirectory, '-l', $databaseLog, '-o', "-p $databasePort", 'start')
                $databaseStarted = $true
                Wait-Port $databasePort $true
            }
            $helper = Join-Path $helperDirectory 'Wayfarer.WaypointBrowserFixture.dll'
            Invoke-Checked 'dotnet.exe' @($helper, 'cleanup', $manifestPath)
            Invoke-Checked 'dotnet.exe' @($helper, 'verify-cleanup', $manifestPath)
        } catch { $cleanupFailures.Add($_.Exception) }
    }
    if ($databaseStarted -and (Test-Path -LiteralPath $databaseDirectory)) {
        try { Invoke-Checked (Join-Path $postgresBin 'pg_ctl.exe') @('-D', $databaseDirectory, '-m', 'fast', 'stop') } catch { $cleanupFailures.Add($_.Exception) }
    }
    if ($databasePort) { try { Wait-Port $databasePort $false } catch { $cleanupFailures.Add($_.Exception) } }
    foreach ($name in $ownedVariables) { [Environment]::SetEnvironmentVariable($name, $originalVariables[$name], 'Process') }
    try {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        $resolvedLocal = [IO.Path]::GetFullPath((Join-Path $repository '.local')) + [IO.Path]::DirectorySeparatorChar
        if (!$resolvedRunRoot.StartsWith($resolvedLocal, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to remove non-run-owned path $resolvedRunRoot" }
        if (Test-Path -LiteralPath $resolvedRunRoot) { Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force }
    } catch { $cleanupFailures.Add($_.Exception) }
}

if ($originalFailure -and $cleanupFailures.Count -gt 0) {
    throw [AggregateException]::new('Browser execution and cleanup both failed.', @($originalFailure) + @($cleanupFailures))
}
if ($originalFailure) { throw $originalFailure }
if ($cleanupFailures.Count -gt 0) { throw [AggregateException]::new('Browser cleanup failed.', $cleanupFailures) }
Write-Host "#407 browser run $runId passed with verified zero residue and free ports."
