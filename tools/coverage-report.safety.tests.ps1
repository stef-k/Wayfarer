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
        $reportDirectory = New-CoverageReportDirectory -CoverageRoot $coverageRoot -RunId $runId
        if ($reportDirectory -cne (Join-Path $coverageRoot $runId)) { throw "Unexpected report directory." }
    }

    Remove-Item -LiteralPath $coverageRoot -Recurse -Force
    Set-Content -LiteralPath $coverageRoot -Value "file"
    Assert-Rejected "coverage root that is a file" { New-CoverageReportDirectory -CoverageRoot $coverageRoot -RunId $runId | Out-Null }
    Remove-Item -LiteralPath $coverageRoot -Force

    $reparseTarget = Join-Path $temporaryRoot "reparse-target"
    New-Item -ItemType Directory -Path $reparseTarget | Out-Null
    New-Item -ItemType Junction -Path $coverageRoot -Target $reparseTarget | Out-Null
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

    Assert-Pass "workflow never removes report output" {
        $workflow = Get-Content -LiteralPath (Join-Path $PSScriptRoot "coverage-report.ps1") -Raw
        if ($workflow -match '(?is)Remove-Item[^\r\n]*(resolvedOutput|report|coverageRoot)') { throw "Workflow contains report-output removal." }
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}

Write-Host "Safety checks: $passed passed, $failed failed."
if ($failed -ne 0) { exit 1 }
