---
title: Import and export
---

# Import and export

Bring location history into Wayfarer, save a copy of your records or move Trip
plans between tools. Location imports add recorded history; Trip imports add
plans. Choose the matching workflow rather than treating every map file as the
same kind of data.

## Import location history

1. Open **Location imports** from your name menu and choose **Upload New File**.
2. Select the file and its **File Type**. Choose the actual format, not merely a
   different filename extension.
3. Decide whether to continue filling missing addresses after import. This is
   optional and requires a configured [personal provider](location-providers.md).
4. Choose **Upload File**, then review progress on the imports page.
5. When processing finishes, open [Timeline or All Locations](timeline.md) and
   check a few dates, coordinates and details from the file.

Large files process in the background, so you can leave the page and return later.
Your administrator controls the upload-size limit and whether uploads are enabled.
Check the displayed limit before uploading a large export.

## Choose a supported format

| Location-history format | Suitable input |
| --- | --- |
| **Google Timeline JSON** | Supported Google location-history exports. |
| **Wayfarer GeoJSON** | Location-history exports from Wayfarer or WayfarerMobile, including their Wayfarer properties. |
| **CSV** | Tabular location records with the expected column headers. |
| **GPX** | GPS tracks and waypoints with recorded times and coordinates. |
| **KML** | Location-history placemarks, including Wayfarer history exports. |

Generic GeoJSON is not supported for new location-history uploads. Google My Maps
and Wayfarer **Trip** KML belong in the [Trip importer](#move-trip-plans), not this
history workflow.

For CSV, the required columns are `Latitude`, `Longitude` and `TimestampUtc`.
Use decimal latitude/longitude coordinates and timestamps with an explicit UTC
or timezone offset. For example:

```csv
Latitude,Longitude,TimestampUtc
37.9838,23.7275,2026-06-01T08:00:00Z
```

A Wayfarer CSV export is a useful template when preparing your own file. Preserve
quoted notes and address text; do not split fields on commas by hand.

## Follow progress and control an import

The imports list shows status, processed records, the last processed record and
errors. Records carrying portable identifiers are recognized by those identifiers;
other records are checked for matching times and coordinates within a small
tolerance. An import can finish with fewer new records than its source contains
because of duplicates or invalid rows.

- **Stop** pauses processing. Already imported locations remain in your history.
  Wait for **Stopping** to become **Stopped** before restarting it.
- **Start** resumes a stopped or failed import.
- **Delete** removes the import entry and uploaded file. It does **not** delete
  the locations already added to your Timeline.

If an import fails, inspect the reported error and progress before retrying; some
records may already be saved. Confirm the format, CSV headers and timestamps. If
progress appears stuck, refresh once and contact your administrator if it persists.
If an uploaded file cannot be removed, ask the administrator to investigate rather
than assuming that deleting the row removed every copy.

## Optionally fill missing addresses

The upload option **Continue filling missing addresses after the import completes**
starts a separate enrichment task. You can also use **Start** under
**Missing-address enrichment** later.

Import completion means history processing has finished, not that every address
has been found. Enrichment can take days for a large history and can pause when a
provider is unavailable or its usage limit is reached. Your imported locations
remain available throughout.

Cancelling enrichment does not cancel or delete an import. Deleting an import
entry does not stop enrichment or erase locations. The provider guide owns the
[enrichment controls and address-repair choices](location-providers.md#address-enrichment-controls).

## Export location history

Open **All Locations** and choose **Export Location Data**. Pick the format for
the tool you will use:

| Export | Use it for |
| --- | --- |
| **Wayfarer GeoJSON** | GIS tools and reusable Wayfarer location history. |
| **CSV** | Spreadsheets, data processing and inspection. |
| **GPX** | GPS-track tools, with Wayfarer extensions for extra details. |
| **KML** | Map viewers such as Google Earth, with extended location details. |

Keep an untouched export when using another tool to transform the data. These
files can include precise positions, times, notes and device metadata. Store and
share them as private history unless you intend otherwise. A data export is a
portable copy of your location or Trip data, not a complete server backup.

## Move Trip plans

From your personal **Trips** list:

- **Import Trip** reads Wayfarer-native KML. If the Trip already exists in your
  account, review the choice to update it or create a separate copy before
  proceeding.
- **Import My Maps** reads Google My Maps KML. Review the imported structure and
  choose transport profiles where needed for duration estimates; the source may
  not contain reliable transport information.
- Export **Wayfarer KML** when you want to preserve a plan for reimport into
  Wayfarer. Export **Google My Maps KML** when compatibility with that tool is
  the priority.
- Export a **PDF Guide** for a printable itinerary with notes, map snapshots,
  distances and links. Follow generation progress and cancel if you no longer
  need it. Links to external searches or maps contact those services when opened.

For planning, routes and visits, see [Trips](trips.md). Downloading a Trip to
[Mobile](mobile.md#download-trip-content) is a separate action from exporting a file.

## Metadata and format compatibility

Wayfarer history formats carry available accuracy, speed, altitude, heading,
activity, source, notes and address information. Wayfarer exports can also retain
provider display text and supported mapped-feature information. Older files remain
accepted, but another tool may drop unfamiliar fields. Supplied addresses are
retained rather than silently rewritten by import.

The published Mobile app uses its existing full-address fields. Its local import
and export do not promise to retain newer server-only address fields such as
`ProviderAddressLine1`. Keep the original server export if you need that detail.

Native Trip KML preserves Regions, Places, Areas, ordered From/Via/To connections,
route geometry and automatic/manual duration choices. Older Wayfarer KML remains
supported. Creating a new copy keeps a loop's shared endpoint and waypoint order
consistent. Distances for imported manual geometry are recalculated; import does
not call a routing provider to invent a route.

Generic interchange formats cannot promise Wayfarer's full planning semantics.
Route coordinates alone do not establish which saved Places are Via stops.
Supported note formatting is retained, but arbitrary imported HTML layouts and
styles do not have exact visual fidelity. For custom integrations and exact fields,
use the [API reference](../development/api.md).

## Recover Mobile queue and local history safely

Use this section when moving to another phone or delivering queued records through
a file. Normal [Mobile synchronization](mobile.md#synchronization-and-offline-records)
does not require these recovery steps.

### Know which copy you are recovering

| Copy | What it contains | What an import changes |
| --- | --- | --- |
| **Pending delivery queue** | Phone records waiting for confirmed delivery to Wayfarer, with delivery status. | A history import does not recreate or clear this queue. |
| **Phone Timeline** | History stored locally for browsing on that phone. | Mobile Timeline import changes this local history only. |
| **Wayfarer server history** | Records confirmed on your instance. | Web location import changes server history only. |

Exporting recovery data does not clear the queue. Do not uninstall, clear app data,
clear pending records or lower a queue limit in a way that discards undelivered
records to fix synchronization.

### Deliver queued records through a server import

1. On the original phone, open **Settings > Offline Queue** and choose **Prepare
   recovery**. Wait until delivery is suspended and active work has finished.
2. Export a recovery **CSV** or **GeoJSON**. Keep the original file intact. Export
   also establishes the suspended-delivery boundary and leaves the queue in place.
3. In Wayfarer's web **Location imports**, import CSV with **CSV**, or the recovery
   GeoJSON with **Wayfarer GeoJSON**.
4. Wait for the import's final result. Inspect errors and verify the imported
   server history before proceeding. A failed or partial import needs inspection
   or retry, not queue clearing.
5. On the original phone, use **Resume and reconcile**. Wayfarer recognizes records
   already imported for the same user by their portable identifiers; missing
   records can upload normally. Confirmed queue rows become synced and follow
   ordinary retention.

The portable GUID is carried as `IdempotencyKey`. Removing or changing it can
prevent exact reconciliation. Use the same Wayfarer instance and user account for
the import and resumed delivery. A success message about exporting a file is not
confirmation that the server has those records.

### Restore history on another phone too

Import the recovery file into the replacement phone's **Timeline** and verify that
local history separately. Also import it into Wayfarer and verify server history
as above. These are **two imports**: neither one repairs the other copy, and neither
recreates the original delivery queue. Resume and reconcile the original phone
only if its queue still exists.

Recovery files can include locations, times, notes, activity/check-in information,
device/app/OS/battery metadata, delivery diagnostics and identifiers. Transfer them
securely and retain them until both histories are verified. Remove unnecessary
copies afterward.

[User guide](index.md) · [Documentation home](../README.md)
