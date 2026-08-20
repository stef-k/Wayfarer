$CoverageOutputMarkerName = ".wayfarer-coverage-output"
$CoverageOutputMarkerValue = "wayfarer:tools/coverage-report.ps1:v1"

function Get-CanonicalPath {
    param([string]$BasePath, [string]$Path)

    return [IO.Path]::GetFullPath($Path, [IO.Path]::GetFullPath($BasePath))
}

function Test-PathWithin {
    param([string]$ParentPath, [string]$CandidatePath, [switch]$AllowEqual)

    $parent = [IO.Path]::GetFullPath($ParentPath).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $candidate = [IO.Path]::GetFullPath($CandidatePath).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ($AllowEqual -and $candidate.Equals($parent, [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    $prefix = $parent + [IO.Path]::DirectorySeparatorChar
    return $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-OrdinaryPathAncestors {
    param([string]$RootPath, [string]$CandidatePath)

    $root = [IO.Path]::GetFullPath($RootPath)
    $candidate = [IO.Path]::GetFullPath($CandidatePath)
    if (-not (Test-PathWithin -ParentPath $root -CandidatePath $candidate -AllowEqual)) {
        throw "Path must remain under $root`: $candidate"
    }

    if (Test-Path -LiteralPath $root) {
        $rootItem = Get-Item -LiteralPath $root -Force
        if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Coverage paths cannot use a reparse-point root: $root"
        }
        if (-not $rootItem.PSIsContainer) {
            throw "Coverage path root is not a directory: $root"
        }
    }

    $relative = [IO.Path]::GetRelativePath($root, $candidate)
    $current = $root
    foreach ($component in $relative.Split([IO.Path]::DirectorySeparatorChar, [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $component
        if (-not (Test-Path -LiteralPath $current)) {
            break
        }

        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Coverage paths cannot pass through a reparse point: $current"
        }
        if (-not $item.PSIsContainer -and -not $current.Equals($candidate, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Coverage path ancestor is not a directory: $current"
        }
    }
}

function Assert-NoReparseTree {
    param([string]$DirectoryPath)

    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($DirectoryPath)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Coverage output contains a reparse point and cannot be replaced: $($item.FullName)"
            }
            if ($item.PSIsContainer) {
                $pending.Push($item.FullName)
            }
        }
    }
}

function Test-CoverageOutputPath {
    param(
        [string]$RepoRoot,
        [string]$OutputDir,
        [switch]$RequireOwnership
    )

    $repository = [IO.Path]::GetFullPath($RepoRoot)
    $coverageRoot = Join-Path $repository "coverage-report"
    $candidate = Get-CanonicalPath -BasePath $repository -Path $OutputDir
    if (-not (Test-PathWithin -ParentPath $coverageRoot -CandidatePath $candidate -AllowEqual)) {
        throw "Coverage output must be coverage-report or one of its descendants: $candidate"
    }

    Assert-OrdinaryPathAncestors -RootPath $repository -CandidatePath $candidate
    if (Test-Path -LiteralPath $candidate) {
        $item = Get-Item -LiteralPath $candidate -Force
        if (-not $item.PSIsContainer) {
            throw "Coverage output must be a directory: $candidate"
        }
    }

    if ($RequireOwnership) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Container)) {
            throw "Existing coverage output directory was expected: $candidate"
        }
        $markerPath = Join-Path $candidate $CoverageOutputMarkerName
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
            throw "Existing coverage output is not owned by this script: $candidate"
        }
        $marker = Get-Item -LiteralPath $markerPath -Force
        if (($marker.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            (Get-Content -LiteralPath $markerPath -Raw) -cne $CoverageOutputMarkerValue) {
            throw "Coverage output ownership marker is invalid: $markerPath"
        }
        Assert-NoReparseTree -DirectoryPath $candidate
    }

    return $candidate
}

function New-CoverageOutputMarker {
    param([string]$OutputPath)

    $markerPath = Join-Path $OutputPath $CoverageOutputMarkerName
    Set-Content -LiteralPath $markerPath -Value $CoverageOutputMarkerValue -NoNewline
}

function Test-RunResultsPath {
    param([string]$ResultsRoot, [string]$RunResultsDir, [string]$RunId)

    $root = [IO.Path]::GetFullPath($ResultsRoot)
    $candidate = [IO.Path]::GetFullPath($RunResultsDir)
    $expected = Join-Path $root $RunId
    if (-not $candidate.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Current-run results path must be the exact generated run directory: $candidate"
    }
    Assert-OrdinaryPathAncestors -RootPath $root -CandidatePath $candidate
    if (Test-Path -LiteralPath $candidate) {
        $item = Get-Item -LiteralPath $candidate -Force
        if (-not $item.PSIsContainer) {
            throw "Current-run results path must be a directory: $candidate"
        }
        Assert-NoReparseTree -DirectoryPath $candidate
    }
    return $candidate
}
