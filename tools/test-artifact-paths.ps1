# Reject links/reparse points in every ancestor and descendant before recursive cleanup.
function Assert-OrdinaryTestTree {
    param([string]$Path)
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if (-not $item) { return }
    $ancestor = $item
    while ($ancestor) {
        if (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Test artifact path contains a reparse point: $($ancestor.FullName)"
        }
        $ancestor = if ($ancestor.PSIsContainer) { $ancestor.Parent } else { $ancestor.Directory }
    }
    if ($item.PSIsContainer) {
        foreach ($child in Get-ChildItem -LiteralPath $Path -Force) {
            Assert-OrdinaryTestTree -Path $child.FullName
        }
    }
}

# Removes only a derived direct child, never a caller-selected recursive root.
function Remove-OwnedTestDirectory {
    param([string]$Root, [string]$Name)
    if (-not $Name -or [IO.Path]::GetFileName($Name) -cne $Name -or $Name -in @('.', '..')) {
        throw 'Expected one owned child name.'
    }
    $candidate = Join-Path $Root $Name
    if (-not (Test-Path -LiteralPath $candidate)) { return }
    try {
        Assert-OrdinaryTestTree -Path $candidate
        if (-not (Get-Item -LiteralPath $candidate -Force).PSIsContainer) { throw 'Expected an ordinary directory.' }
        Remove-Item -LiteralPath $candidate -Recurse -Force
    } catch {
        # Launcher and teardown may complete the same exact removal concurrently.
        if (Test-Path -LiteralPath $candidate) { throw }
    }
}
