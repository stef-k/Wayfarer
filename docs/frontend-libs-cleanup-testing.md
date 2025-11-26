# Frontend Libraries Cleanup - Testing Guide

This document provides testing procedures to verify that the cleanup of `wwwroot/lib` did not break any functionality.

## What Was Done

The following changes were made to `wwwroot/lib`:

1. **Deleted entire directories:**
   - `bootstrap-icons-old/` - Unused old version of bootstrap icons
   - `quill-2.0.3/` - Source code directory (runtime files kept in `quill/`)

2. **Cleaned up dev artifacts from libraries:**
   - `Leaflet.markercluster-1.4.1/` - Removed package.json, bower.json, build configs, src/, test/, example/ directories
   - `davidshimjs-qrcodejs/` - Removed .gitignore, bower.json, example HTML files, bundled jquery

3. **Files kept:**
   - All runtime JS/CSS files (minified and source versions)
   - All fonts and images referenced by CSS
   - All LICENSE files
   - Source maps (.map files) for debugging

## Automated Testing

Run the PowerShell script to verify all library assets are accessible:

```powershell
# Start the application first (in a separate terminal)
dotnet run

# Then run the test script (in another terminal)
.\test-lib-assets.ps1

# Or specify a custom URL
.\test-lib-assets.ps1 -BaseUrl "http://localhost:5000"
```

The script tests all 43 library assets referenced in the application.

**Expected Result:** All assets should return 200 OK.

## Manual Testing Checklist

After starting the application (`dotnet run`), perform the following manual tests:

### 1. Authentication & Forms (jQuery Validation)

**Pages to test:**
- `/Identity/Account/Login`
- `/Identity/Account/Register`
- `/Admin/Users/Create`
- `/Manager/Users/Create`

**What to check:**
- [ ] Page loads without JavaScript errors (check browser Console)
- [ ] Form validation works (try submitting empty form)
- [ ] Validation messages appear for required fields
- [ ] Client-side validation triggers before submission

**Key libraries tested:** jQuery, jQuery Validation, jQuery Validation Unobtrusive

---

### 2. Map Functionality (Leaflet & Plugins)

**Pages to test:**
- `/User/Trips` (any trip edit page)
- `/User/Groups/Map`
- `/Manager/Groups/Map`
- Any public trip view page

**What to check:**
- [ ] Page loads without JavaScript errors (check browser Console)
- [ ] Map tiles load correctly
- [ ] Markers appear on the map
- [ ] Marker clustering works (zoom in/out to test)
- [ ] Drawing tools work (if applicable)
- [ ] Map pans and zooms smoothly
- [ ] Custom controls appear correctly

**Key libraries tested:** Leaflet, Leaflet.markercluster, leaflet-draw, leaflet-image, leaflet-drag, leaflet-editable, turf

---

### 3. Rich Text Editor (Quill)

**Pages to test:**
- Any trip or place editing page with a rich text editor
- Trip description editor
- Place notes editor

**What to check:**
- [ ] Page loads without JavaScript errors (check browser Console)
- [ ] Quill editor toolbar appears correctly
- [ ] Text formatting works (bold, italic, lists, etc.)
- [ ] Editor styling is correct (snow theme CSS loaded)

**Key libraries tested:** Quill

---

### 4. QR Code Generation

**Pages to test:**
- `/Identity/Account/Manage/EnableAuthenticator`
- `/User/ApiToken/Index`

**What to check:**
- [ ] Page loads without JavaScript errors (check browser Console)
- [ ] QR code generates and displays correctly
- [ ] QR code can be scanned (test with a mobile device if needed)

**Key libraries tested:** davidshimjs-qrcodejs

---

### 5. Bootstrap UI Components

**Pages to test:**
- Any page with Bootstrap components (most pages)
- Pages with modals, dropdowns, tooltips

**What to check:**
- [ ] Page loads without JavaScript errors (check browser Console)
- [ ] Bootstrap CSS loaded (check Network tab for `/lib/bootstrap/dist/css/bootstrap.min.css`)
- [ ] Modals open and close correctly
- [ ] Dropdowns work
- [ ] Tooltips/popovers work (if using Tippy.js)
- [ ] Responsive design works (resize browser)
- [ ] Bootstrap icons display correctly

**Key libraries tested:** Bootstrap, Bootstrap Icons, Popper.js, Tippy.js

---

### 6. Sortable Lists

**Pages to test:**
- Any page with drag-and-drop sorting functionality
- Trip segment ordering
- Place ordering in regions

**What to check:**
- [ ] Page loads without JavaScript errors (check browser Console)
- [ ] Drag-and-drop works
- [ ] Items reorder correctly
- [ ] Order persists after save

**Key libraries tested:** SortableJS

---

## Browser DevTools Checks

For each page tested, verify in browser DevTools:

### Network Tab
- [ ] No 404 errors for `/lib/...` resources
- [ ] All JS/CSS files load with 200 status
- [ ] Font files load correctly (check for .woff, .woff2)
- [ ] SVG icons load if referenced

### Console Tab
- [ ] No JavaScript errors
- [ ] No "Failed to load resource" errors
- [ ] No warnings about missing libraries

### Elements/Inspector Tab
- [ ] CSS styles apply correctly
- [ ] Bootstrap icons render (check if they use pseudo-elements with font)
- [ ] Custom fonts load correctly

---

## Regression Testing Summary

After completing all manual tests, verify:

1. **No new JavaScript errors** appeared in any tested page
2. **No 404 errors** for library assets in Network tab
3. **All interactive features work** as expected
4. **Visual styling is correct** on all pages
5. **Forms validate correctly** with client-side validation
6. **Maps render and function** properly
7. **Rich text editor works** correctly
8. **QR codes generate** successfully

---

## What to Do If Tests Fail

If any test fails:

1. **Check browser Console** for specific error messages
2. **Check Network tab** for 404 errors - note which asset is missing
3. **Verify the file exists** in `wwwroot/lib/`
4. **Check the file path** in the view or JS file matches the actual location
5. **Restore from git** if a required file was accidentally deleted:
   ```bash
   git restore wwwroot/lib/path/to/file
   ```

---

## Dependabot Verification

After this cleanup is merged, verify in GitHub (or your git hosting platform):

1. Go to Dependabot alerts (if using GitHub)
2. Confirm that alerts for packages in `wwwroot/lib/*/package.json` no longer appear
3. No new Dependabot alerts should be created for deleted package.json files

---

## Summary of Libraries After Cleanup

All libraries now contain **only runtime assets**:

| Library | Location | Files Kept |
|---------|----------|------------|
| Bootstrap | `bootstrap/dist/` | CSS, JS (min + maps) |
| Bootstrap Icons | `bootstrap-icons/` | CSS, fonts, SVG icons |
| jQuery | `jquery/dist/` | JS (min + maps) |
| jQuery Validation | `jquery-validation/dist/` | JS (min + maps) |
| jQuery Validation Unobtrusive | `jquery-validation-unobtrusive/dist/` | JS (min + maps) |
| Leaflet | `leaflet/` | JS, CSS, images |
| Leaflet MarkerCluster | `Leaflet.markercluster-1.4.1/dist/` | JS, CSS |
| Leaflet Plugins | `leaflet-*/` | JS files only |
| Popper.js | `popperjs/` | JS (min) |
| Tippy.js | `tippy/` | JS (min) |
| SortableJS | `sortablejs/` | JS (min) |
| Turf.js | `turf/` | JS (min) |
| Quill | `quill/` | JS, CSS |
| QRCode.js | `davidshimjs-qrcodejs/` | JS files |

**Total reduction:** Removed ~2000+ files of dev artifacts while keeping all runtime assets intact.
