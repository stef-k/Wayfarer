---
title: WayfarerMobile
---

# WayfarerMobile

Use WayfarerMobile to record and browse location history, follow Groups, adjust
your own Trips and navigate on your phone. You need a reachable Wayfarer instance
and an account with the **User** role. For browser account setup, start with the
[user guide](index.md).

## Get the published app

Download the Android APK from [WayfarerMobile releases](https://github.com/stef-k/WayfarerMobile/releases).
The currently published app is [Android 1.3.0](https://github.com/stef-k/WayfarerMobile/releases/tag/1.3.0).
There is no published iOS build. This guide describes the published Android app.

Install the APK using Android's installer; you may need to allow installation from
the browser or file manager you used. When upgrading, install over the existing
app. Keep app data intact so local history, queued work and downloaded Trips remain
available. Avoid uninstalling or clearing data as a troubleshooting step.

## Connect to your instance

1. Open the web app's **Settings → Connect apps** and follow
   [Get a connection token](connect-apps.md#get-a-connection-token). Keep the one-time
   reveal open while setting up the phone.
2. In Mobile's setup wizard, choose **Scan QR Code** and scan the Web QR.
   Alternatively, enter the server address and connection token in the wizard,
   then choose **Save Configuration**.
3. Check that the address is your intended instance's HTTPS address. To reconnect,
   open **Settings → Permissions & Setup → Rerun Setup Wizard**. When not connected,
   **Settings → Account** also offers **Scan QR Code to Connect**.
4. Confirm access with an authenticated action, such as loading your own Trips or
   making a deliberate check-in and finding it in the Web Timeline. The setup
   probe loads public server settings; it does not prove the bearer token is valid.
5. Grant the location permissions Android requests when you enable recording.
   Background tracking needs the applicable background permission too.

A Wayfarer connection token is not a Geoapify or Mapbox key. Personal provider credentials
are configured in the web app and stay on the server. If your token is revoked or
your account is disabled, correct that connection before expecting delivery to
resume. Replacing a token does not confirm delivery of pending data.

## Record locations

### Automatic tracking

Enable **Timeline Tracking** for automatic location recording. The app follows
the instance's capture thresholds for time, movement and accuracy; it does not
necessarily upload every position the phone reports. You can inspect the server's
effective thresholds in the web **Settings** page.

Keep Android location permission enabled and review battery restrictions if
background recording stops. Poor indoor reception can produce readings that do
not meet the accuracy threshold. Turning tracking off stops new automatic capture,
but does not delete local or server history or withdraw previously shared records.

### Manual check-ins

Use the check-in action to record a point intentionally. Choose an activity or
notes when useful. Manual check-ins bypass the automatic time and distance filters.
They can still fail if your API token is invalid, the server is temporarily busy or
the submitted location is invalid.

Inspect the phone's recording/delivery status, then check the web Timeline when
you need confirmation that a record reached Wayfarer. An offline record and a
confirmed server record are different states.

## Browse your phone Timeline

Open **Timeline** to browse retained history by day. Move between days, pick a
date or return to **Today**, then select a location to inspect its time, position,
activity and notes. Local history appears first, with a server refresh when online.
Offline browsing covers only records retained on the phone; it is not a complete
download of your server history or proof that a recording was delivered.

Do not edit or delete imported or unconfirmed phone records in Android 1.3.0:
an action can affect a different server record. Make corrections in
[web Locations](timeline.md#add-or-correct-a-location) once the record is present
in your server history.

For a spreadsheet, GIS tool or another phone, use the ordinary
[Timeline CSV/GeoJSON import and export](import-export.md#move-your-phone-timeline).

## Follow Groups and live updates

Open **Groups** to view memberships and inspect permitted members' positions.
Accept or decline invitations in the [Web Invitations workflow](groups.md#create-or-join-a-group),
then reload Groups on the phone. Group and visit updates arrive while connected.
Timestamps matter: a member's last recorded location may be old.

The published app retries transient connection failures, but a live connection
that closes can leave updates stopped. Reopen the affected view or restart the app
if updates stop despite a working connection. Correct token/access problems when
reported rather than treating old positions as live. See [Group sharing](groups.md)
for visibility rules; public Timeline privacy does not govern Group access.

### Navigate toward a Group member

Select a member's latest shared position on the Group map and use the navigation
action in their details. Choose [Wayfarer hosted routing or Direct guidance](#hosted-routing-and-direct-guidance),
or open external maps. Check the position's timestamp before setting off:
navigation targets that selected position, not a continuously followed person.
Selecting a historical marker instead targets that older position.

## View Trips and navigate

Open a Trip to explore Regions, Places, Areas and Segments, including notes and
travel information. Create and restructure plans in the [web Trip editor](trips.md).
The published app retains ordered intermediate Places in Segment content online
and in downloaded Trips. Details show Start, Via and End; this does not mean Mobile
provides every web editing tool.

Each Segment has **Show on map** independently of opening its details. Hiding it
removes its map line and decorations while keeping Place markers and active
navigation. Choices last for the loaded Trip and reset when you unload or switch
Trips.

If details say **Straight endpoint connection — route geometry unavailable**, the
line is a display aid, not a calculated road route or a navigation path. Unavailable
waypoint or route-point counts do not establish that a route is complete.

For directions to a Place or dropped pin, open **Directions** and choose the kind
of guidance you want. Opening external navigation passes the selected destination
to your device's navigation app.

### Hosted routing and Direct guidance

Valid saved Segment geometry takes priority for following the plan. When a new
route is needed, Mobile can ask your authenticated Wayfarer server for hosted
routing using your configured personal directions provider. Choose an offered
provider mode. Current Wayfarer supports Geoapify directions; Mapbox Directions is
not available.

Provider routing modes are separate from a Segment's transport profile. Configure
and verify directions in the [provider settings guide](location-providers.md#directions-and-routing).
Mobile sends the request through Wayfarer; it does not receive the provider key or
contact a public routing service as an automatic fallback.

A matching, validated route previously obtained from Wayfarer can be retained on
the phone for offline reuse. Choose retained guidance when offered, explicitly
request a fresh route, or choose **Direct**. Failure or cancellation of a fresh
request preserves prior retained guidance.

**Direct** provides straight-line distance and bearing without a routing request.
It remains available without a provider or network connection when the required
position and destination are available. It is not road routing. Provider
unavailability affects this routing choice; it does not disable authentication,
location delivery, saved Segment geometry or Direct guidance.

### Edit your own Trip

In a loaded Trip's overview, use **+ Add** to add a Region or Place. A Place's
edit action lets you change its name, position, notes, marker styling or Region.
Other available edit actions update Trip/Region names and travel notes.

Supported changes can wait as pending work while offline. Check the synchronization
status in **My Trips** after reconnecting; a local change is not yet a confirmed
server change, and failed changes need review. Use the [web editor](trips.md) to
create Trips and draw Areas or Segment routes.

### Visit alerts and spoken cues

In **Settings > Visit Notifications**, enable alerts for server-detected visits
to planned Places. Choose **Banner**, **Voice** or **Both**; enable **Voice
Announcement** to hear the Place name. Alerts can arrive after synchronization
and depend on connectivity and Android permissions. Review the evidence in
[Trip visit history](trips.md#record-and-review-visits).

Under **Settings > Navigation**, enable **Audio Announcements** for spoken cues
such as approaching a waypoint, going off route and arrival. The guidance follows
the selected route; manually drawn geometry does not guarantee road instructions.

## Download Trip content

Before traveling, use **Download** on a Trip to save its metadata, Places, notes,
Areas, Segments, intermediate Places and route geometry locally. Confirm the
download has completed and open it before depending on it away from the network.

Away from the network, follow available saved Segment geometry or matching retained
guidance, or use Direct with a known position and destination. Notes remain readable,
but remote images and external links can still need a connection.

Downloaded content remains useful offline. A Trip download does **not** download
a complete raster basemap, and locally retained route guidance is separate from
the Trip's saved geometry. Downloading the plan does not guarantee that every
destination has a calculated route from wherever you later start.

## Understand map caching

The published Mobile basemap requests OpenStreetMap tiles directly as the map
renders the area and zoom levels you view. It caches those tiles locally. Mobile
does not use the web application's server-side tile provider/cache settings for
this basemap and has no bulk Trip-area tile download or prefetch-radius setting.

Open **Settings > Map Cache** to set its size limit, inspect usage or clear cached
tiles. Clearing this cache does not remove downloaded Trips. Previously viewed
tiles may remain usable while cached, but they can expire or be removed: complete
offline basemap coverage is not promised. A blank offline map can coexist with
usable downloaded Places and routes. Respect the
[OpenStreetMap tile usage policy](https://operations.osmfoundation.org/policies/tiles/).

## Synchronization and offline records

Locations recorded without connectivity can wait in the phone's delivery queue.
When delivery is active and the connection works, the app retries pending work
subject to server rules. Inspect **Settings > Offline Queue** for delivery state,
counts and capacity; limited storage and queue limits matter on long offline trips.

The queue, the phone's local Timeline and confirmed Wayfarer history are separate.
A local Timeline import does not upload that history or recreate a delivery queue;
a web import does not fill the phone's Timeline. Do not clear a queue merely
because you exported its records.

For moving phones or delivering queued history through a file, follow
[Mobile recovery and reconciliation](import-export.md#recover-mobile-queue-and-local-history-safely).
Verify both local and server history before removing any recovery copies.

## Privacy and security

Wayfarer's authoritative history is stored on your instance's server. Mobile also
stores local history, pending work, downloaded Trips, cached map tiles and retained
guidance needed for its operation. Protect the phone and any exported files.

For an optional access prompt, set up **Settings > Security > PIN Lock**. This
does not encrypt exported files or replace Android's device protection.

Optional external providers receive the coordinates, searches or route inputs
needed for enabled features. Tile services receive tile requests. Opening external
navigation shares the selected destination with that app, and external links may
contact their own services. Personal provider credentials remain protected on the
server and are never sent to WayfarerMobile.

Use your intended HTTPS instance, keep the connection token and pairing QR private,
and [replace an exposed token](connect-apps.md#get-a-connection-token). If connection testing fails, check the
address in a browser, connectivity, token and account status. For server-side
problems, contact your instance administrator.

[User guide](index.md) · [Documentation home](../README.md)
