param(
    [string]$OutputDir = "coverage-report"
)

$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot

Write-Host "Restoring tools..."
dotnet tool restore | Out-Null

$runSettings = Join-Path $repoRoot "coverlet.runsettings"
$testProject = "tests/Wayfarer.Tests/Wayfarer.Tests.csproj"

Write-Host "Building tests..."
dotnet build $testProject -c Debug | Out-Null

Write-Host "Running tests with XPlat Code Coverage (runsettings)..."
dotnet test $testProject -c Debug --settings "$runSettings" --collect:"XPlat Code Coverage" | Out-Null

$coverageFile = Get-ChildItem -Path "tests/Wayfarer.Tests/TestResults" -Recurse -Filter "coverage.cobertura.xml" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $coverageFile) {
    Write-Error "Coverage report not found under tests/Wayfarer.Tests/TestResults"
    exit 1
}

Write-Host "Generating HTML report to $OutputDir..."
dotnet reportgenerator `
    "-reports:$($coverageFile.FullName)" `
    "-targetdir:$OutputDir" `
    "-reporttypes:Html" `
    "-assemblyfilters:+Wayfarer;-AspNetCoreGeneratedDocument*;-WayfarerAspNetCoreGeneratedDocument*" `
    "-filefilters:-*Migrations*;-*Areas/Identity/Pages/*;-*\Models\Dtos\*;-*\Models\ViewModels\*;-*Views/*;-*.cshtml;-*.cshtml.cs;-*.cshtml.g.cs" | Out-Null

Write-Host "Coverage report generated at $OutputDir/index.html"
