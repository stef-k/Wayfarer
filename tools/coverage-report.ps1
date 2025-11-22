param(
    [string]$OutputDir = "coverage-report"
)

$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot

Write-Host "Restoring tools..."
dotnet tool restore | Out-Null

$coverageDir = "tests/Wayfarer.Tests/TestResults/coverage"
Write-Host "Running tests with coverage..."
dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj `
    /p:CollectCoverage=true `
    /p:CoverletOutput=$coverageDir/ `
    /p:CoverletOutputFormat=cobertura `
    /p:Exclude=\"[WayfarerAspNetCoreGeneratedDocument*]*\" | Out-Null

$coverageFile = Join-Path $coverageDir "coverage.cobertura.xml"
if (-not (Test-Path $coverageFile)) {
    Write-Error "Coverage report not found at $coverageFile"
    exit 1
}

Write-Host "Generating HTML report to $OutputDir..."
dotnet reportgenerator `
    "-reports:$coverageFile" `
    "-targetdir:$OutputDir" `
    "-reporttypes:Html" | Out-Null

Write-Host "Coverage report generated at $OutputDir/index.html"
