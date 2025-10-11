# TODO List

Each subtitle has a priority number with 1 being the first one to be implemented.

## Cleanup: Remove Itinero-based routing generation (no longer needed), priority 1

Summary
- The Itinero/OsmSharp-based routing graph generation feature (.routing files) is out of scope. Remove related packages, services, controllers, and config while keeping unrelated mapping/tile and trip features intact.

Scope And Impact Analysis
- Packages to remove (Wayfarer.csproj)
  - Itinero (Itinero)
  - Itinero.IO.Osm (Itinero.IO.Osm)
- Files/services to delete
  - Services/RoutingBuilderService.cs (uses Itinero + OsmSharp to build .routing files)
  - Services/RoutingCacheService.cs (manages .routing/.osm.pbf cache)
  - Services/Helpers/GeofabrikCountryIndexService.cs (downloads Geofabrik index and resolves .osm.pbf URLs)
  - Areas/Api/Controllers/RoutingController.cs (exposes /api/routing endpoints)
- DI registrations to remove (Program.cs)
  - AddSingleton<GeofabrikCountryIndexService>()
  - AddScoped<RoutingCacheService>()
  - AddScoped<RoutingBuilderService>()
- App settings to remove (or deprecate)
  - appsettings*.json: CacheSettings:RoutingCacheDirectory, CacheSettings:OsmPbfCacheDirectory
  - Note: CacheSettings:TileCacheDirectory must remain (used by tile cache)
- Admin settings page (Areas/Admin/Controllers/SettingsController.cs)
  - Currently computes sizes for "RoutingCache" and "OsmPbfCache" paths and shows stats via ViewData
  - Options:
    1) Remove these stats entirely (preferred) OR
    2) Guard with feature flag and hide if directories not present
  - Tile cache stats remain: do not remove TileCacheService usage
- Trip DTOs/Controllers
  - TripBoundaryDto previously had a RoutingFile placeholder; removed as part of cleanup
  - Segment.RouteGeometry (LineString) is unrelated to Itinero and must remain; user-drawn/edited routes still supported

Step-by-step Plan
1) Remove package references
   - Edit Wayfarer.csproj and remove:
     - <PackageReference Include="Itinero" ... />
     - <PackageReference Include="Itinero.IO.Osm" ... />
   - Run: dotnet restore; dotnet build (verify no missing types)

2) Remove routing feature code
   - Delete files:
     - Services/RoutingBuilderService.cs
     - Services/RoutingCacheService.cs
     - Services/Helpers/GeofabrikCountryIndexService.cs
     - Areas/Api/Controllers/RoutingController.cs

3) Remove DI registrations (Program.cs)
   - Remove lines adding:
     - GeofabrikCountryIndexService (singleton)
     - RoutingCacheService (scoped)
     - RoutingBuilderService (scoped)
   - Build to confirm DI container resolves

4) Remove/clean configuration keys
   - appsettings.json and appsettings.Development.json:
     - Remove CacheSettings:RoutingCacheDirectory
     - Remove CacheSettings:OsmPbfCacheDirectory
   - Keep CacheSettings:TileCacheDirectory and any other tile settings

5) Admin Settings page cleanup
   - Areas/Admin/Controllers/SettingsController.cs:
     - Remove routing/pbf size calculations and ViewData: RoutingPath, RoutingSizeMB, RoutingFileCount, PbfPath, PbfSizeMB, PbfFileCount, RoutingPbfTotalMB/GB
     - Remove combined storage that includes routing/pbf; if needed, recompute combined storage using only TileCache + Uploads
   - Areas/Admin/Views/Settings/Index.cshtml (if it references the above ViewData): remove/hide routing/pbf rows/cards

6) Search and verify no dangling references
   - rg "RoutingCacheService|RoutingBuilderService|GeofabrikCountryIndexService|/api/routing|.routing|OsmPbfCache|RoutingCacheDirectory"
   - Remove or update any remaining references/comments

7) Sanity checks
   - Build and run locally
   - Verify:
     - /api/routing endpoints no longer exist (404)
     - Admin Settings page loads without routing/pbf sections
     - TripBoundary and TripsController still work; RouteGeometry in segments unaffected
     - Tile cache features continue to work

8) Documentation
   - Update README/AGENTS.md notes (if any) mentioning routing graph files
   - Optional: note deprecation in CHANGELOG or release notes

Risk Notes
- GeofabrikCountryIndexService is only used by routing code path (comment mentions MbtileCacheService but no such service exists in code). Safe to remove.
- Segment.RouteGeometry and any GeoJSON serialization logic remain; unrelated to Itinero.
- Admin Settings had passive directory size checks; removing them avoids stale UI. No functional dependency elsewhere.

Rollback
- Perform cleanup in a dedicated PR/branch. If issues arise (e.g., unexpected reference), revert the file deletions and DI removals, then re-scope.

## Trusted Managers Mechanism, priority 3

Implement a trusted user/manager mechanism to allow managers see user location data.

### Implementation List

* Add necessary database mechanism to link managers with users, possible a many to many table
* Create the UI in user's settings to add/delete trusted manager(s)
* Implement the manager interface to select a user and see his location data.

## Geofencing, priority 3

### Implementation List

* Create the UI in both User and Manager areas to create and store geofence areas
* Implement geofence queries in User and Manager areas

## Manager Fleet Tracking System, priority 4

### Implementation List

* Create the UI in manager's area to allow managers track vehicle location data
* Create the necessary database mechanism to link managers with vehicles, a possible many to many table
* Design the system so that a manager can only add/remove vehicles from his organization which leads to:
* Managers and Vehicles should have an additional DB field Organization as a unique identifying field linking them
