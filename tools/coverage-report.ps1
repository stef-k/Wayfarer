param(
    [string]$OutputDir = "coverage-report"
)

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
$testResultsRoot = Join-Path $repoRoot "tests/Wayfarer.Tests/TestResults/coverage-report"
$runId = [Guid]::NewGuid().ToString("N")
$runResultsDir = Test-RunResultsPath -ResultsRoot $testResultsRoot -RunResultsDir (Join-Path $testResultsRoot $runId) -RunId $runId
$resolvedOutputDir = Test-CoverageOutputPath -RepoRoot $repoRoot -OutputDir $OutputDir

try {
    Write-Host "Building tests..."
    Invoke-CheckedDotnet build $testProject -c Debug

    Write-Host "Running ordinary tests with XPlat Code Coverage (runsettings)..."
    Invoke-CheckedDotnet test $testProject -c Debug --no-build `
        --settings $runSettings `
        --collect:"XPlat Code Coverage" `
        --results-directory $runResultsDir `
        --filter "Category!=RequiresSpatialite&Category!=RequiresPlaywright"

    $coverageFiles = @(Get-ChildItem -LiteralPath $runResultsDir -Recurse -Filter "coverage.cobertura.xml")
    if ($coverageFiles.Count -ne 1 -or $coverageFiles[0].Length -eq 0) {
        throw "Expected one non-empty Cobertura file for this run under $runResultsDir; found $($coverageFiles.Count)."
    }

    if (Test-Path -LiteralPath $resolvedOutputDir) {
        Test-CoverageOutputPath -RepoRoot $repoRoot -OutputDir $resolvedOutputDir -RequireOwnership | Out-Null
        Remove-Item -LiteralPath $resolvedOutputDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolvedOutputDir | Out-Null
    New-CoverageOutputMarker -OutputPath $resolvedOutputDir

    Write-Host "Generating HTML report to $resolvedOutputDir..."
    Invoke-CheckedDotnet reportgenerator `
        "-reports:$($coverageFiles[0].FullName)" `
        "-targetdir:$resolvedOutputDir" `
        "-reporttypes:Html" `
        "-assemblyfilters:+Wayfarer;-AspNetCoreGeneratedDocument*;-WayfarerAspNetCoreGeneratedDocument*" `
        "-filefilters:-*Migrations*;-*Areas/Identity/Pages/*;-*\Models\Dtos\*;-*\Models\ViewModels\*;-*Views/*;-*.cshtml;-*.cshtml.cs;-*.cshtml.g.cs"

    $htmlIndex = Join-Path $resolvedOutputDir "index.html"
    if (-not (Test-Path -LiteralPath $htmlIndex) -or (Get-Item -LiteralPath $htmlIndex).Length -eq 0) {
        throw "ReportGenerator did not create a non-empty HTML report at $htmlIndex."
    }
    Test-CoverageOutputPath -RepoRoot $repoRoot -OutputDir $resolvedOutputDir -RequireOwnership | Out-Null

    Write-Host "Cobertura consumed: $($coverageFiles[0].FullName) ($($coverageFiles[0].Length) bytes)"
    Write-Host "HTML report: $htmlIndex ($((Get-Item -LiteralPath $htmlIndex).Length) bytes)"
}
finally {
    if (Test-Path -LiteralPath $runResultsDir) {
        Test-RunResultsPath -ResultsRoot $testResultsRoot -RunResultsDir $runResultsDir -RunId $runId | Out-Null
        Remove-Item -LiteralPath $runResultsDir -Recurse -Force
        Write-Host "Removed current-run results: $runResultsDir"
    }
}
