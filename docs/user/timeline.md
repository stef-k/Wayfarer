---
title: Timeline and locations
---

# Timeline and locations

Use your Timeline to explore where you have been, correct individual records and
choose what to share publicly. Each **Location** is one recorded position at a
particular time. Locations can come from WayfarerMobile tracking, GPSLogger,
compatible apps, manual check-ins, files or entries you add in the web app.
See [Connect apps](connect-apps.md) to set up live capture from an app.

Trips are plans rather than history. To plan destinations and record visits to
them, use the [Trips guide](trips.md).

## Browse your history

Open your name menu and choose the view that fits your task:

- **Timeline**: explore Day, Week, Month, Year or a Custom range of recorded history.
- **My Private Timeline**: explore your own history on the map.
- **Locations**: work with a map and table together.
- **All Locations**: search the table, edit records and export history.

In **Timeline**, ordinary entry opens today's Day map. Choose a date to commit Day
immediately, or choose Week and activate a date to open its containing week. The
marked calendar loads only when opened. ● means matching records; ? means presence
is unknown, including while marks load or fail. Empty and unknown dates remain
selectable. The week order follows the effective browser formatting locale, with a
labelled Monday fallback. A committed Week retains its original week start.

Browse calendar months/years without changing the map. **Show this month/year**
commits that period. **Custom** opens separate inclusive From/Through inputs with
**Apply range / Cancel**. Its calendar edits only the labelled endpoint. Previous
range / Next range shift by the complete inclusive length. Day/month/year shortcuts
retain the civil anchor and clamp before constructing a shorter month. From Custom,
those shortcuts explicitly enter Day using From. Today enters Day and keeps filters.

**Calendar basis** chooses Recorded-location calendar (first-use default) or My
time zone. The explicit viewer zone stays frozen until you apply a change; capture
membership uses each record's qualified location zone. Both clocks remain visible
in detail. Navigation admits periods starting on/before the browsing horizon within
1900–2100. Full weeks/months/years and Custom Through can extend past that horizon;
results use the full range. Invalid ranges are rejected, never shortened.

**Filters…** optionally combines Search Notes and address, Activities/No activity
and a saved map rectangle. Search is a normalized literal phrase across existing
Notes/address fields, not fuzzy or Notes-only search. Activity alternatives are OR;
the dimensions and dates combine AND. **Use this map area** captures the current
viewport once. Pan/zoom never changes it; explicitly replace or clear it. Apply
commits, Cancel discards the filter draft, and visible chips clear individual
dimensions. Clear filters keeps the date period, camera and connection rules.

Private filters stay out of URLs. An opaque local context restores them after a
same-tab reload or **Save & Return** from Edit. The tab stores one current intent,
not records or prior queries. A copied context link without that matching local
state stops reads and offers **Re-enter filters** or **Continue without filters**.
A clean date-only link deliberately opens unfiltered. If storage fails, Apply keeps
the prior filters and your new draft. Tab closure/cross-device restoration and
secure erasure are not promised; browser session recovery or duplicate tabs may
retain independent copies. Observed account/session loss clears this Timeline intent,
while protected mutation drafts keep their original account authority.

The map remains the default. Day shows every mapped matching record; larger
periods can sample over 3,000 known matches (newest five per geohash-5 cell, up to
3,000). **Records** offers unsampled known matches in every period, including those
without a map position, through explicit **Load more**. Loaded rows, the separately
observed known count and unresolved candidates are distinct. Pages observe live
history separately: inserts/moves can evade traversal, even after the last page.
Refresh or a successful mutation restarts the traversal. Row/marker detail uses the
same exact identity; selection never refits the camera. **Locate** and **Fit period**
are explicit actions. Missing rows do not establish deletion.

**Distance between recorded locations** loads with Recorded path Off. Known gaps
in evidence show **Distance in available segments — Partial**; unknown source order
shows **Distance unavailable**, never a fabricated zero. Coverage, refresh/failure
and geometry availability are separate. **Connections & distance** explains as-of,
source/counts and exact applied rules. Maximum gap starts at 2 hours; the optional
speed heuristic starts at 1,260 km/h. Hours/Minutes and km/h edits convert once;
untouched rounded text preserves exact saved rules. Apply saves the whole browser
preference before recalculating; Cancel discards that rule draft; Reset changes only
the draft until Apply. Closing the dialog retains unfinished rules for this tab.
Rules and remembered basis are local to this browser/account, without cloud sync.

Recorded paths approximate stored-location connections, with excluded/uncertain
intermediates breaking continuity. Sampling and drawing never change analytical
distance. Large geometry can be unavailable while distance remains useful. Active
time is not calculated. **Refresh / Retry** and **Cancel calculation** retain scope,
camera, selected identity and independent drafts. Refresh never recaptures the area.
Public, Group, embedded and all-history views keep their existing meanings.

### Search and filter locations

Open **Search** in the Locations table. Combine a date range with activity,
address, country, region, city or text in notes. Latitude and longitude fields help
find records around a position. Choose **Search** to apply the filters and
**Clear** to remove them.

![Location table with date, activity, address and notes filters](../images/all-locations-search.JPG)

Locations retain their recorded time and local-time information when available.
Check the displayed timezone when comparing a phone record with a web record,
especially across travel or daylight-saving changes.

## Add or correct a location

1. Open **All Locations** and choose **Add Location**, or choose **Edit** for an
   existing record.
2. Set the coordinates and timestamp. Choose an activity and add notes if useful.
3. Review the address fields and save.

You can also change an activity from the location details or table without opening
the full form. Choose Save in the inline editor to apply the selected activity.

Saves compare the fields you originally observed. A conflicting change keeps your
draft; independent fields can still save. If a reply is lost, Save checks the
retained attempt before submitting another action. A successful save completes
normally. Keep this browser's saved site data while recovering a draft. If local
storage fails, the page reports that it could not save the draft; keep it open.
Conflict review shows your originals, draft and current facts before you explicitly
adopt new originals. An old receipt cannot consume newer typing, toolbar Clear,
image insertion or a map pick. These changes are retained before you press Save.
Reopening an unresolved inline Activity edit restores your desired selection;
after success, a later edit starts from fresh originals. Inline Activity saves
also keep an unsent full-form draft: reopening the form restores your Notes and
other edits. If you select another Activity or Clear during a pending save, the
older result leaves that newer choice open and retained for its own Save.

The existing Location **Edit** page uses a rich Notes toolbar for headings, bold,
italic, underline, native lists, links and Default/Serif/Monospace fonts. Keyboard
undo/redo and list indentation work within the current editing session. **Remove
formatting** keeps text, links and images. **Clear** is a separate request to clear
Notes; deleting all text is an empty edit. Opening/focusing Notes or saving another
field keeps the original Notes. Undoing back to the initial document also restores
the unchanged value. Older formatting may need adjustment when deliberately edited.

Use **Link** to add/edit/unlink or explicitly **Follow link**. Links never navigate
when clicked in the editing surface. The URL dialog supports Escape and returns
focus to Notes. Use **Image** with an absolute HTTP(S) URL. Images load through the
server proxy; failed previews retain their URL and never retry directly against the
origin. Pasting or dropping files or unsupported image URLs rejects the whole
transfer with feedback and leaves Notes unchanged. Ordinary text mentioning data
images still pastes normally. Location Create, Bulk Notes,
Visit and Trip editors retain their existing controls until the next migration.

![Location Edit Notes toolbar with a retained link and proxied image](../images/location-tiptap.png)

Moving a point preserves the exact capture instant and updates the local display
for the new zone. Explicit time editing uses the zone shown when the form opened;
the draft keeps that zone and its chosen daylight-saving occurrence across reloads,
even if another writer moves the point. Choose an occurrence for an ambiguous
daylight-saving time. If a retained draft says its time zone is unavailable,
review or re-enter **Local Date and Time**, choose the earlier/later occurrence
when needed, and enter the intended zone (for example, `Europe/Athens` or `UTC`).
Choose **Review time and zone** and confirm the displayed local time, zone and UTC
instant. Confirmation retains this interpretation before Save; earlier uncertain
actions still need recovery. Cancel keeps the retained draft and sends nothing.
If you have not changed the time, you can save Notes and other fields without
confirming a zone. Moving also discards
obsolete address information and attempts one authorized replacement. Optional
lookup failure still saves valid coordinates. Explicit manual components, including
Clear, win over lookup results. Manual-only address editing makes no provider call.

A per-record delete confirms observed stored content. Changed content conflicts;
damaged/unavailable display facts remain visible and do not make the row undeletable.
After rejection, opening Delete again shows previously observed and current content
for a new confirmation. A pending action keeps its original consent; reopening the
confirmation cannot authorize deleting changed content while it might still execute.

Use **Bulk Edit Notes** for deliberate changes across matching records. Matching
and append use current locked values at Apply time; preview counts can change.
Replace and Clear overwrite current Notes. Selected bulk deletion removes current
owned rows even if their content changed, including when you select one row. A
lost bulk reply is uncertain: refresh history and decide deliberately before
submitting again. There is no automatic replay; append may duplicate a suffix.
Export history you need to keep before deleting.

Server execution authority and historical receipts are finite evidence. They
never expire phone captures, drafts or pending obligations. Companion mobile
source/native delivery remains separately owned.

### Read location details

Details can include activity, notes, coordinates, address, accuracy, speed,
altitude, heading and source. Missing metadata means it was not available in that
record; it does not imply a zero value.

Some enriched locations show structured address components and a separate
**Nearby mapped feature**, **Mapped area** or **Mapped feature**. A nearby business
or building is context, not proof that you visited it. **Street address
unavailable** or **Address details unavailable** means the lookup did not supply
that detail; retained provider display text may still be shown.

The **Wiki** action looks for related Wikipedia articles. Opening an article
contacts Wikipedia. Optional address lookup and repair are explained in
[Personal location providers](location-providers.md#address-enrichment-controls).

## Understand your statistics

Timeline summaries show the number of locations, the first and last dates and
recorded countries, regions and settlements. Choose **Geographic statistics** in the calendar Timeline, or the country/region/city
links in all-history summaries, to open geographic details. Expand **Country → Region
→ city/settlement** to see record counts and first/last recorded dates. A **Map**
action returns you to that geography on the map. Counts measure recorded positions,
not confirmed visits to venues; use [Trip visits](trips.md#record-and-review-visits)
for the separate planned-Place workflow.

Geographic counts depend on the address labels saved with each location. Different
spellings can split the same place, and missing labels are not invented. **Country
not recorded** and **Region not recorded** group records with missing parent
labels; they do not count as additional countries or regions.

Your private views summarize their own history or the complete selected date
period, even when a large month/year map shows only sampled points. Public
Timeline summaries cover all history eligible for public viewing, including the
delay and Hidden Areas rules. Moving or zooming the public map does not change
that full-history summary. A public Timeline with no eligible locations has zero
counts and no first or last date.

## Make your Timeline public deliberately

Choose **Private** for no public Timeline, public **Now** to share without a delay,
or a delayed public Timeline to share only older locations. [Hidden Areas](#protect-sensitive-places-with-hidden-areas)
exclude sensitive positions independently of that delay.

Your Timeline starts private. To publish it:

1. Open **Settings > Manage Timeline Settings**.
2. Optionally set a title, then enable **Make Timeline Public**.
3. Choose a **Time Threshold** and save.
4. Open the public link shown in **Settings**, preferably while signed out, to
   check what a visitor can see.

The threshold is a delay: **1 day** shows locations at least a day old. First-time
public sharing defaults to a one-day delay if you have not chosen one. **Now**
shares without a delay and requires explicit confirmation that your current
location will be publicly available.

Presets include one hour, day, week, month and year. For a custom delay, use a
number and unit: `h` hours, `d` days, `w` weeks, `m` months or `y` years. For example,
`6h`, `2w` or `1.5d`. Month and year delays are approximations of 30 and 365 days.
If settings report an invalid saved threshold, choose and save a valid one before
expecting public sharing to work.

Public details can expose precise positions, times, addresses, activities and
notes. A delay reduces immediacy but still reveals history and routines. Making
the Timeline private again stops public access through Wayfarer; it cannot recall
copies somebody has already saved.

## Protect sensitive places with Hidden Areas

Open **Hidden Areas** from your name menu. Add a named area and draw a polygon
around a sensitive location such as home or work. Save it, then check the public
Timeline. Edit the polygon if you need a wider boundary.

Locations within Hidden Areas are excluded from the public Timeline, its embed
and its public statistics. Overlapping areas work together. Deleting a Hidden
Area makes those records eligible for public viewing again, subject to the delay.

![Hidden Areas list with edit and delete controls](../images/hidden-areas.JPG)

Hidden Areas do not delete records from your private history. They do not prevent
capture or provider address lookup, hide locations in trusted Groups, or hide
Places and visit progress in a public Trip. Set those sharing choices separately.

## Share a link or embed the Timeline

When your Timeline is public, **Settings** shows its public link and offers **Copy
embed URL** and **Copy embed HTML**. Open Wayfarer at the public HTTPS address
people will use before copying. Paste the generated iframe into your website;
adjust its height to fit the page.

The embed follows the same public visibility rules. Ordinary wheel scrolling and
single-finger swipes scroll the surrounding page. Use Ctrl + wheel (Cmd on macOS)
to zoom, mouse drag to pan, or two fingers to pan and zoom on touchscreens. **Open
full view** opens the regular public Timeline in a new tab.

For a copy of your history or to add older records, see
[Import and export](import-export.md). For sharing with named people, see
[Groups](groups.md).

[User guide](index.md) · [Documentation home](../README.md)
