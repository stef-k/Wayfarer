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
$excludeAssemblies = "[Wayfarer]AspNetCoreGeneratedDocument*"

Write-Host "Running tests with coverage..."
dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj `
    /p:CollectCoverage=true `
    "/p:CoverletOutput=$coverageDir\coverage" `
    /p:CoverletOutputFormat=cobertura `
    "/p:Exclude=$excludeAssemblies" `
    "/p:ExcludeByFile=**\\obj\\**%3b**\\bin\\**%3b**\\Migrations\\*.cs%3b**\\Areas\\Identity\\Pages\\Account\\Login.cshtml.cs%3b**\\Areas\\Identity\\Pages\\Account\\Logout.cshtml.cs%3b**\\Areas\\Identity\\Pages\\Account\\AccessDenied.cshtml.cs%3b**\\Areas\\Identity\\Pages\\Account\\RegisterConfirmation.cshtml.cs%3b**\\Areas\\Identity\\Pages\\Account\\ForgotPassword*.cshtml.cs%3b**\\Areas\\Identity\\Pages\\Account\\ResetPassword*.cshtml.cs%3b**\\Areas\\Identity\\Pages\\Account\\Manage\\**%3b**\\Areas\\Identity\\Pages\\Error.cshtml.cs%3b**\\Areas\\Identity\\Pages\\Shared\\**%3b**\\Models\\ViewModels\\**\\*.cs%3b**\\Models\\Dtos\\**\\*.cs" | Out-Null

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
    "-filefilters:-*\\ViewModels\\*;-*\\Dtos\\*;-*\\Migrations\\*;-*Areas\\Identity\\Pages\\*" | Out-Null

Write-Host "Coverage report generated at $OutputDir/index.html"
