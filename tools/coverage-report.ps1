param(
    [string]$OutputDir = "coverage-report"
)

$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot

Write-Host "Restoring tools..."
dotnet tool restore | Out-Null

$coverageDir = Join-Path $repoRoot "tests/Wayfarer.Tests/TestResults/coverage"
if (-not (Test-Path $coverageDir)) { New-Item -ItemType Directory -Path $coverageDir | Out-Null }
$coverageFile = Join-Path $coverageDir "coverage.cobertura.xml"
$excludeAssemblies = "[Wayfarer]AspNetCoreGeneratedDocument*;[*]Migrations.*;[*]ViewModels.*;[*]Dtos.*"
$excludeFiles = "**/obj/**;**/bin/**;**/Migrations/*.cs;**/Areas/Identity/Pages/**/*.cshtml.cs;**/Models/ViewModels/**/*.cs;**/Models/Dtos/**/*.cs"
$testProject = "tests/Wayfarer.Tests/Wayfarer.Tests.csproj"
$testDll = Join-Path $repoRoot "tests/Wayfarer.Tests/bin/Debug/net9.0/Wayfarer.Tests.dll"

Write-Host "Building tests..."
dotnet build $testProject -c Debug | Out-Null

if (Test-Path $coverageFile) { Remove-Item $coverageFile -Force }

Write-Host "Running tests with coverlet.console..."
dotnet tool run coverlet `
    "$testDll" `
    --target "dotnet" `
    --targetargs "test $testProject --no-build" `
    --output "$coverageFile" `
    --format cobertura `
    --exclude "$excludeAssemblies" `
    --exclude-by-file "$excludeFiles" | Out-Null

if (-not (Test-Path $coverageFile)) {
    Write-Error "Coverage report not found at $coverageFile"
    exit 1
}

Write-Host "Generating HTML report to $OutputDir..."
dotnet reportgenerator `
    "-reports:$coverageFile" `
    "-targetdir:$OutputDir" `
    "-reporttypes:Html" `
    "-assemblyfilters:+Wayfarer;-AspNetCoreGeneratedDocument*;-WayfarerAspNetCoreGeneratedDocument*" `
    "-filefilters:-*ViewModels*;-*Dtos*;-*Migrations*;-*Areas/Identity/Pages/*" | Out-Null

Write-Host "Coverage report generated at $OutputDir/index.html"
