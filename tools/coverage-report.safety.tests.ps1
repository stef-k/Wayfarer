$ErrorActionPreference = "Stop"

$scriptUnderTest = Join-Path $PSScriptRoot "coverage-report-paths.ps1"
. $scriptUnderTest

$passed = 0
$failed = 0
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("wayfarer-coverage-safety-" + [Guid]::NewGuid().ToString("N"))
$repositoryRoot = Join-Path $temporaryRoot "repository"
$externalRoot = Join-Path $temporaryRoot "external"

function Assert-Accepted {
    param(
        [string]$Name,
        [string]$OutputDir,
        [bool]$RequireOwnership = $false
    )

    try {
        Test-CoverageOutputPath -RepoRoot $repositoryRoot -OutputDir $OutputDir -RequireOwnership:$RequireOwnership | Out-Null
        $script:passed++
        Write-Host "PASS: $Name"
    }
    catch {
        $script:failed++
        Write-Error "FAIL: $Name should be accepted: $($_.Exception.Message)" -ErrorAction Continue
    }
}

function Assert-Rejected {
    param(
        [string]$Name,
        [string]$OutputDir,
        [bool]$RequireOwnership = $false
    )

    try {
        Test-CoverageOutputPath -RepoRoot $repositoryRoot -OutputDir $OutputDir -RequireOwnership:$RequireOwnership | Out-Null
        $script:failed++
        Write-Error "FAIL: $Name should be rejected." -ErrorAction Continue
    }
    catch {
        $script:passed++
        Write-Host "PASS: $Name rejected: $($_.Exception.Message)"
    }
}

try {
    New-Item -ItemType Directory -Path $repositoryRoot, $externalRoot | Out-Null
    foreach ($directory in @("tools", ".git", "docs", "tests", "unrelated", "coverage-report")) {
        New-Item -ItemType Directory -Path (Join-Path $repositoryRoot $directory) | Out-Null
    }

    $sentinel = Join-Path $repositoryRoot "unrelated/sentinel.txt"
    Set-Content -LiteralPath $sentinel -Value "preserve"
    $ownedOutput = Join-Path $repositoryRoot "coverage-report/owned"
    New-Item -ItemType Directory -Path $ownedOutput | Out-Null
    Set-Content -LiteralPath (Join-Path $ownedOutput ".wayfarer-coverage-output") -Value $CoverageOutputMarkerValue -NoNewline
    Set-Content -LiteralPath (Join-Path $ownedOutput "coverage.cobertura.xml") -Value "generated"
    New-Item -ItemType Junction -Path (Join-Path $repositoryRoot "coverage-report/escape") -Target $externalRoot | Out-Null

    Assert-Rejected "repository root" $repositoryRoot
    Assert-Rejected "parent traversal outside repository" (Join-Path $repositoryRoot "../outside")
    Assert-Rejected "tools" (Join-Path $repositoryRoot "tools")
    Assert-Rejected ".git" (Join-Path $repositoryRoot ".git")
    Assert-Rejected "docs" (Join-Path $repositoryRoot "docs")
    Assert-Rejected "tests" (Join-Path $repositoryRoot "tests")
    Assert-Rejected "unrelated repository directory" (Join-Path $repositoryRoot "unrelated")
    Assert-Rejected "junction escape" (Join-Path $repositoryRoot "coverage-report/escape/output")
    Assert-Rejected "unowned coverage directory" (Join-Path $repositoryRoot "coverage-report") -RequireOwnership $true

    $boundaryMarker = Join-Path $repositoryRoot "coverage-report/.wayfarer-coverage-output"
    Set-Content -LiteralPath $boundaryMarker -Value "malformed" -NoNewline
    Assert-Rejected "malformed ownership marker" (Join-Path $repositoryRoot "coverage-report") -RequireOwnership $true
    Remove-Item -LiteralPath $boundaryMarker -Force
    New-Item -ItemType Junction -Path $boundaryMarker -Target $externalRoot | Out-Null
    Assert-Rejected "reparse-point ownership marker" (Join-Path $repositoryRoot "coverage-report") -RequireOwnership $true
    Remove-Item -LiteralPath $boundaryMarker -Force

    $fileOutput = Join-Path $repositoryRoot "coverage-report/file-output"
    Set-Content -LiteralPath $fileOutput -Value "not a directory"
    Assert-Rejected "file used as output directory" $fileOutput

    Remove-Item -LiteralPath (Join-Path $repositoryRoot "coverage-report") -Recurse -Force
    New-Item -ItemType Directory -Path (Join-Path $repositoryRoot "coverage-report") | Out-Null
    Assert-Accepted "default fresh coverage output" (Join-Path $repositoryRoot "coverage-report")
    Assert-Accepted "fresh permitted descendant" (Join-Path $repositoryRoot "coverage-report/fresh/child")

    New-Item -ItemType Directory -Path $ownedOutput | Out-Null
    Set-Content -LiteralPath (Join-Path $ownedOutput ".wayfarer-coverage-output") -Value $CoverageOutputMarkerValue -NoNewline
    Set-Content -LiteralPath (Join-Path $ownedOutput "coverage.cobertura.xml") -Value "generated"
    Assert-Accepted "owned existing generated output" $ownedOutput -RequireOwnership $true
    New-Item -ItemType Junction -Path (Join-Path $ownedOutput "nested-escape") -Target $externalRoot | Out-Null
    Assert-Rejected "owned output containing a reparse point" $ownedOutput -RequireOwnership $true

    $runId = [Guid]::NewGuid().ToString("N")
    $resultsRoot = Join-Path $repositoryRoot "tests/Wayfarer.Tests/TestResults/coverage-report"
    $runResults = Join-Path $resultsRoot $runId
    New-Item -ItemType Directory -Path $runResults | Out-Null
    Test-RunResultsPath -ResultsRoot $resultsRoot -RunResultsDir $runResults -RunId $runId | Out-Null
    $passed++
    Write-Host "PASS: exact current-run results directory"
    try {
        Test-RunResultsPath -ResultsRoot $resultsRoot -RunResultsDir (Join-Path $resultsRoot "other") -RunId $runId | Out-Null
        $failed++
        Write-Error "FAIL: unrelated results directory should be rejected." -ErrorAction Continue
    }
    catch {
        $passed++
        Write-Host "PASS: unrelated results directory rejected: $($_.Exception.Message)"
    }

    if ((Get-Content -LiteralPath $sentinel -Raw).Trim() -ne "preserve") {
        throw "The unrelated sentinel changed during validation."
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Host "Safety checks: $passed passed, $failed failed."
if ($failed -ne 0) {
    exit 1
}
