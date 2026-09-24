# Validates the internally generated identifier without normalizing malformed input.
function Test-CoverageRunId {
    param([string]$RunId)

    [Guid]$parsedRunId = [Guid]::Empty
    if (-not [Guid]::TryParseExact($RunId, "N", [ref]$parsedRunId) -or
        $parsedRunId.ToString("N") -cne $RunId) {
        throw "Coverage run ID must be an exact N-format GUID: $RunId"
    }

    return $RunId
}

# Derives both run-owned paths from repository roots and the same validated identifier.
function Get-CoverageRunPaths {
    param([string]$RepoRoot, [string]$RunId)

    Test-CoverageRunId -RunId $RunId | Out-Null
    $repository = [IO.Path]::GetFullPath($RepoRoot)
    $reportRoot = Join-Path $repository "coverage-report"
    $resultsRoot = Join-Path $repository "tests/Wayfarer.Tests/TestResults/coverage-report"

    return [PSCustomObject]@{
        ReportRoot       = $reportRoot
        ReportDirectory  = Join-Path $reportRoot $RunId
        ResultsRoot      = $resultsRoot
        ResultsDirectory = Join-Path $resultsRoot $RunId
    }
}

# Creates only a fresh report child beneath an ordinary repository coverage root.
function New-CoverageReportDirectory {
    param([string]$CoverageRoot, [string]$RunId)

    Test-CoverageRunId -RunId $RunId | Out-Null
    $root = [IO.Path]::GetFullPath($CoverageRoot)
    if (-not (Test-Path -LiteralPath $root)) {
        New-Item -ItemType Directory -Path $root | Out-Null
    }

    $rootItem = Get-Item -LiteralPath $root -Force
    if (-not $rootItem.PSIsContainer) {
        throw "Coverage report root must be a directory: $root"
    }
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Coverage report root cannot be a reparse point: $root"
    }

    $reportDirectory = Join-Path $root $RunId
    if (Test-Path -LiteralPath $reportDirectory) {
        $existing = Get-Item -LiteralPath $reportDirectory -Force
        if (($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Coverage report child cannot be a reparse point: $reportDirectory"
        }
        throw "Coverage report child already exists: $reportDirectory"
    }

    New-Item -ItemType Directory -Path $reportDirectory | Out-Null
    return $reportDirectory
}

# Allows cleanup only for the exact ordinary results child owned by this run.
function Test-CoverageResultsCleanupPath {
    param([string]$ResultsRoot, [string]$ResultsDirectory, [string]$RunId)

    Test-CoverageRunId -RunId $RunId | Out-Null
    $root = [IO.Path]::GetFullPath($ResultsRoot)
    $candidate = [IO.Path]::GetFullPath($ResultsDirectory)
    $expected = [IO.Path]::GetFullPath((Join-Path $root $RunId))
    if (-not $candidate.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Current-run results path must be the exact generated run directory: $candidate"
    }

    $rootItem = Get-Item -LiteralPath $root -Force
    if (-not $rootItem.PSIsContainer -or
        ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Coverage results root must be an ordinary directory: $root"
    }

    $childItem = Get-Item -LiteralPath $candidate -Force
    if (-not $childItem.PSIsContainer -or
        ($childItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Current-run results must be an ordinary directory: $candidate"
    }

    return $candidate
}

. (Join-Path $PSScriptRoot 'test-artifact-paths.ps1')

# On success retain the validated current report and prune only recognized ordinary siblings.
# On failure remove only this invocation's partial report, preserving all earlier evidence.
function Complete-CoverageReport {
    param([string]$CoverageRoot, [string]$RunId, [bool]$Succeeded)
    Test-CoverageRunId -RunId $RunId | Out-Null
    if (-not $Succeeded) {
        Remove-OwnedTestDirectory -Root $CoverageRoot -Name $RunId
        return
    }
    $current = Join-Path $CoverageRoot $RunId
    Assert-OrdinaryTestTree -Path $current
    $index = Get-Item -LiteralPath (Join-Path $current 'index.html')
    if ($index.PSIsContainer -or $index.Length -eq 0) { throw 'Current HTML report is not complete.' }
    foreach ($child in Get-ChildItem -LiteralPath $CoverageRoot -Force) {
        if ($child.Name -cne $RunId -and $child.Name -cmatch '^[0-9a-f]{32}$' -and
            $child.PSIsContainer -and ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
            try { Remove-OwnedTestDirectory -Root $CoverageRoot -Name $child.Name }
            catch { Write-Warning "Retained unsafe/inaccessible old report: $($_.Exception.Message)" }
        }
    }
}
