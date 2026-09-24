$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "coverage-report-paths.ps1")

$passed = 0
$failed = 0
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("wayfarer-coverage-safety-" + [Guid]::NewGuid().ToString("N"))
$repositoryRoot = Join-Path $temporaryRoot "repository"
$coverageRoot = Join-Path $repositoryRoot "coverage-report"
$resultsRoot = Join-Path $repositoryRoot "tests/Wayfarer.Tests/TestResults/coverage-report"

function Assert-Pass {
    param([string]$Name, [scriptblock]$Test)
    try {
        & $Test
        $script:passed++
        Write-Host "PASS: $Name"
    }
    catch {
        $script:failed++
        Write-Error "FAIL: $Name`: $($_.Exception.Message)" -ErrorAction Continue
    }
}

function Assert-Rejected {
    param([string]$Name, [scriptblock]$Test)
    try {
        & $Test
        $script:failed++
        Write-Error "FAIL: $Name should be rejected." -ErrorAction Continue
    }
    catch {
        $script:passed++
        Write-Host "PASS: $Name rejected: $($_.Exception.Message)"
    }
}

try {
    New-Item -ItemType Directory -Path $repositoryRoot | Out-Null
    $runId = [Guid]::NewGuid().ToString("N")

    Assert-Pass "generated run ID is exact N-format GUID" {
        Test-CoverageRunId -RunId $runId | Out-Null
        if ($runId -cnotmatch '^[0-9a-f]{32}$') { throw "Run ID is not lowercase N format." }
    }
    foreach ($malformed in @("", "not-a-guid", ([Guid]::NewGuid().ToString("D")), $runId.ToUpperInvariant())) {
        Assert-Rejected "malformed run ID '$malformed'" { Test-CoverageRunId -RunId $malformed | Out-Null }
    }

    Assert-Pass "run paths use the same exact GUID child" {
        $paths = Get-CoverageRunPaths -RepoRoot $repositoryRoot -RunId $runId
        if ($paths.ReportDirectory -cne (Join-Path $coverageRoot $runId)) { throw "Unexpected report path." }
        if ($paths.ResultsDirectory -cne (Join-Path $resultsRoot $runId)) { throw "Unexpected results path." }
    }

    Assert-Pass "existing ordinary coverage root is accepted" {
        New-Item -ItemType Directory -Path $coverageRoot | Out-Null
        $previousReport = Join-Path $coverageRoot ([Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $previousReport | Out-Null
        Set-Content -LiteralPath (Join-Path $previousReport "index.html") -Value "previous"
        $reportDirectory = New-CoverageReportDirectory -CoverageRoot $coverageRoot -RunId $runId
        if ($reportDirectory -cne (Join-Path $coverageRoot $runId)) { throw "Unexpected report directory." }
        if (-not (Test-Path -LiteralPath (Join-Path $previousReport "index.html") -PathType Leaf)) { throw "Previous report changed." }
    }

    Remove-Item -LiteralPath $coverageRoot -Recurse -Force
    Set-Content -LiteralPath $coverageRoot -Value "file"
    Assert-Rejected "coverage root that is a file" { New-CoverageReportDirectory -CoverageRoot $coverageRoot -RunId $runId | Out-Null }
    Remove-Item -LiteralPath $coverageRoot -Force

    $reparseTarget = Join-Path $temporaryRoot "reparse-target"
    New-Item -ItemType Directory -Path $reparseTarget | Out-Null
    New-Item -ItemType $(if ($env:OS -eq 'Windows_NT') { 'Junction' } else { 'SymbolicLink' }) -Path $coverageRoot -Target $reparseTarget | Out-Null
    Assert-Rejected "coverage root that is a reparse point" { New-CoverageReportDirectory -CoverageRoot $coverageRoot -RunId $runId | Out-Null }
    Remove-Item -LiteralPath $coverageRoot -Force

    New-Item -ItemType Directory -Path (Join-Path $coverageRoot $runId) | Out-Null
    Assert-Rejected "pre-existing report GUID child" { New-CoverageReportDirectory -CoverageRoot $coverageRoot -RunId $runId | Out-Null }

    $currentResults = Join-Path $resultsRoot $runId
    $siblingResults = Join-Path $resultsRoot ([Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $currentResults, $siblingResults | Out-Null
    Set-Content -LiteralPath (Join-Path $currentResults "current.txt") -Value "remove"
    Set-Content -LiteralPath (Join-Path $siblingResults "sibling.txt") -Value "preserve"

    Assert-Pass "current results GUID is accepted for cleanup" {
        Test-CoverageResultsCleanupPath -ResultsRoot $resultsRoot -ResultsDirectory $currentResults -RunId $runId | Out-Null
    }
    Assert-Rejected "sibling results path" {
        Test-CoverageResultsCleanupPath -ResultsRoot $resultsRoot -ResultsDirectory $siblingResults -RunId $runId | Out-Null
    }
    Assert-Rejected "malformed results run ID" {
        Test-CoverageResultsCleanupPath -ResultsRoot $resultsRoot -ResultsDirectory $currentResults -RunId "bad" | Out-Null
    }

    Assert-Pass "temporary cleanup removes only the current GUID fixture" {
        $validated = Test-CoverageResultsCleanupPath -ResultsRoot $resultsRoot -ResultsDirectory $currentResults -RunId $runId
        Remove-Item -LiteralPath $validated -Recurse -Force
        if (Test-Path -LiteralPath $currentResults) { throw "Current results remain." }
        if (-not (Test-Path -LiteralPath (Join-Path $siblingResults "sibling.txt") -PathType Leaf)) { throw "Sibling results changed." }
    }

    Assert-Pass "failed report preserves previous and unknown entries; success prunes only valid siblings" {
        $previousId = [Guid]::NewGuid().ToString('N')
        $previous = New-CoverageReportDirectory -CoverageRoot $coverageRoot -RunId $previousId
        Set-Content (Join-Path $previous 'index.html') 'previous'
        $unknown = Join-Path $coverageRoot 'manual-evidence'
        New-Item -ItemType Directory -Path $unknown | Out-Null
        $unknownFile = Join-Path $coverageRoot 'notes.txt'
        Set-Content $unknownFile 'manual'
        $guidFile = Join-Path $coverageRoot ([Guid]::NewGuid().ToString('N'))
        Set-Content $guidFile 'not a directory'
        $uppercase = Join-Path $coverageRoot 'ABCDEF0123456789ABCDEF0123456789'
        New-Item -ItemType Directory -Path $uppercase | Out-Null
        $linkedId = [Guid]::NewGuid().ToString('N')
        New-Item -ItemType $(if ($env:OS -eq 'Windows_NT') { 'Junction' } else { 'SymbolicLink' }) -Path (Join-Path $coverageRoot $linkedId) -Target $reparseTarget | Out-Null
        Complete-CoverageReport $coverageRoot $runId $false
        if (!(Test-Path $previous) -or (Test-Path (Join-Path $coverageRoot $runId))) { throw 'Failure retention is incorrect.' }
        $current = New-CoverageReportDirectory $coverageRoot $runId
        Set-Content (Join-Path $current 'index.html') 'current'
        Complete-CoverageReport $coverageRoot $runId $true
        if (!(Test-Path $unknownFile) -or !(Test-Path $guidFile) -or !(Test-Path $uppercase)) { throw 'Unrecognized entries changed.' }
        if ((Test-Path $previous) -or !(Test-Path $current) -or !(Test-Path $unknown) -or !(Test-Path (Join-Path $coverageRoot $linkedId))) { throw 'Success pruning is incorrect.' }
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}

Write-Host "Safety checks: $passed passed, $failed failed."
if ($failed -ne 0) { exit 1 }
