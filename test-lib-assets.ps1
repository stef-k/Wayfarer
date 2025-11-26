# Frontend Library Assets Test Script
# Tests that all referenced /lib/ assets return 200 OK
# Assumes the application is running on http://localhost:5000 (or configure below)

param(
    [string]$BaseUrl = "http://localhost:5000"
)

Write-Host "Testing Frontend Library Assets" -ForegroundColor Cyan
Write-Host "Base URL: $BaseUrl" -ForegroundColor Cyan
Write-Host ""

# List of all library assets referenced in the application
$assets = @(
    # Bootstrap
    "/lib/bootstrap/dist/css/bootstrap.min.css"
    "/lib/bootstrap/dist/js/bootstrap.bundle.min.js"

    # Bootstrap Icons
    "/lib/bootstrap-icons/bootstrap-icons.css"
    "/lib/bootstrap-icons/fonts/bootstrap-icons.woff"
    "/lib/bootstrap-icons/fonts/bootstrap-icons.woff2"
    "/lib/bootstrap-icons/bootstrap-icons-1.13.1/rulers.svg"
    "/lib/bootstrap-icons/bootstrap-icons-1.13.1/link-45deg.svg"
    "/lib/bootstrap-icons/bootstrap-icons-1.13.1/check.svg"

    # jQuery
    "/lib/jquery/dist/jquery.min.js"
    "/lib/jquery-validation/dist/jquery.validate.min.js"
    "/lib/jquery-validation-unobtrusive/dist/jquery.validate.unobtrusive.min.js"

    # Leaflet
    "/lib/leaflet/leaflet-1.9.4.js"
    "/lib/leaflet/leaflet-src.js"
    "/lib/leaflet/leaflet-1.9.4.css"
    "/lib/leaflet/leaflet.draw.js"
    "/lib/leaflet/leaflet.draw.css"
    "/lib/leaflet-image/leaflet-image.js"
    "/lib/leaflet-drag/Path.Drag.js"
    "/lib/leaflet-editable/Leaflet.Editable.min.js"

    # Leaflet MarkerCluster
    "/lib/Leaflet.markercluster-1.4.1/dist/leaflet.markercluster.js"
    "/lib/Leaflet.markercluster-1.4.1/dist/MarkerCluster.css"
    "/lib/Leaflet.markercluster-1.4.1/dist/MarkerCluster.Default.css"

    # Other libraries
    "/lib/popperjs/popper.min.js"
    "/lib/tippy/tippy-bundle.umd.min.js"
    "/lib/sortablejs/Sortable.min.js"
    "/lib/turf/turf.min.js"
    "/lib/quill/quill-2.0.3.js"
    "/lib/quill/quill.snow-2.0.3.css"
    "/lib/davidshimjs-qrcodejs/qrcode.js"
)

$failedAssets = @()
$passedCount = 0
$failedCount = 0

foreach ($asset in $assets) {
    $url = "$BaseUrl$asset"

    try {
        $response = Invoke-WebRequest -Uri $url -Method Head -UseBasicParsing -TimeoutSec 10

        if ($response.StatusCode -eq 200) {
            Write-Host "[PASS] $asset" -ForegroundColor Green
            $passedCount++
        } else {
            Write-Host "[FAIL] $asset (Status: $($response.StatusCode))" -ForegroundColor Red
            $failedAssets += @{Asset = $asset; Status = $response.StatusCode; Error = ""}
            $failedCount++
        }
    } catch {
        Write-Host "[FAIL] $asset (Error: $($_.Exception.Message))" -ForegroundColor Red
        $failedAssets += @{Asset = $asset; Status = "Error"; Error = $_.Exception.Message}
        $failedCount++
    }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Test Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Passed: $passedCount" -ForegroundColor Green
Write-Host "Failed: $failedCount" -ForegroundColor $(if ($failedCount -eq 0) { "Green" } else { "Red" })
Write-Host ""

if ($failedAssets.Count -gt 0) {
    Write-Host "Failed Assets:" -ForegroundColor Red
    foreach ($failed in $failedAssets) {
        Write-Host "  - $($failed.Asset)" -ForegroundColor Red
        if ($failed.Error) {
            Write-Host "    Error: $($failed.Error)" -ForegroundColor Yellow
        } else {
            Write-Host "    Status: $($failed.Status)" -ForegroundColor Yellow
        }
    }
    Write-Host ""
    exit 1
} else {
    Write-Host "All library assets are accessible!" -ForegroundColor Green
    exit 0
}
