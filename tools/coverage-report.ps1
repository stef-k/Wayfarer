$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot
. (Join-Path $PSScriptRoot "coverage-report-paths.ps1")

# Fail immediately when a native tool returns a non-zero exit code.
function Invoke-CheckedDotnet {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

Write-Host "Restoring tools..."
Invoke-CheckedDotnet tool restore

$runSettings = Join-Path $repoRoot "coverlet.runsettings"
$testProject = "tests/Wayfarer.Tests/Wayfarer.Tests.csproj"
$runId = [Guid]::NewGuid().ToString("N")
Test-CoverageRunId -RunId $runId | Out-Null
$runPaths = Get-CoverageRunPaths -RepoRoot $repoRoot -RunId $runId
$reportDirectory = New-CoverageReportDirectory -CoverageRoot $runPaths.ReportRoot -RunId $runId
Write-Host "Coverage report directory: $reportDirectory"

try {
    Write-Host "Building tests..."
    Invoke-CheckedDotnet build $testProject -c Debug

    Write-Host "Running ordinary tests with XPlat Code Coverage (runsettings)..."
    Invoke-CheckedDotnet test $testProject -c Debug --no-build `
        --settings $runSettings `
        --collect:"XPlat Code Coverage" `
        --results-directory $runPaths.ResultsDirectory `
        --filter "Category!=RequiresSpatialite&Category!=RequiresPlaywright"

    $coverageFiles = @(Get-ChildItem -LiteralPath $runPaths.ResultsDirectory -Recurse -Filter "coverage.cobertura.xml")
    if ($coverageFiles.Count -ne 1 -or $coverageFiles[0].Length -eq 0) {
        throw "Expected one non-empty Cobertura file for this run under $($runPaths.ResultsDirectory); found $($coverageFiles.Count)."
    }

    Write-Host "Generating HTML report to $reportDirectory..."
    Invoke-CheckedDotnet reportgenerator `
        "-reports:$($coverageFiles[0].FullName)" `
        "-targetdir:$reportDirectory" `
        "-reporttypes:Html" `
        "-assemblyfilters:+Wayfarer;-AspNetCoreGeneratedDocument*;-WayfarerAspNetCoreGeneratedDocument*" `
        "-filefilters:-*Migrations*;-*Areas/Identity/Pages/*;-*\Models\Dtos\*;-*\Models\ViewModels\*;-*Views/*;-*.cshtml;-*.cshtml.cs;-*.cshtml.g.cs"

    $htmlIndex = Join-Path $reportDirectory "index.html"
    if (-not (Test-Path -LiteralPath $htmlIndex) -or (Get-Item -LiteralPath $htmlIndex).Length -eq 0) {
        throw "ReportGenerator did not create a non-empty HTML report at $htmlIndex."
    }
    Write-Host "Cobertura consumed: $($coverageFiles[0].FullName) ($($coverageFiles[0].Length) bytes)"
    Write-Host "HTML report: $htmlIndex ($((Get-Item -LiteralPath $htmlIndex).Length) bytes)"
}
finally {
    if (Test-Path -LiteralPath $runPaths.ResultsDirectory) {
        $cleanupPath = Test-CoverageResultsCleanupPath -ResultsRoot $runPaths.ResultsRoot -ResultsDirectory $runPaths.ResultsDirectory -RunId $runId
        Remove-Item -LiteralPath $cleanupPath -Recurse -Force
        Write-Host "Removed current-run results: $cleanupPath"
    }
}
