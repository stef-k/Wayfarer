# FAQ

## Is Wayfarer a Hosted Service?

No. Wayfarer is self-hosted: each person or organization deploys its own server and controls its URL and authoritative history. Start with [Getting Started](01-Getting-Started.md) or [Install & Self-Hosting](02-Install-and-Dependencies.md).

## Which Linux Architectures Are Supported?

The guided production installation supports Linux AMD64 and ARM64. Use the bootstrap archive matching your host. 32-bit ARM is unsupported; see [installation prerequisites](02-Install-and-Dependencies.md#guided-production-installation).

## Do I Need .NET, Node, PostgreSQL, Nginx, or Certbot on the Host?

The supported guided installation needs none of those host installations and no source clone. The self-contained operator acquires the matching deployment and exact container images; PostgreSQL/PostGIS, the app, and managed Caddy run in Compose. Source development and advanced native/manual deployment have separate requirements. See [Install & Self-Hosting](02-Install-and-Dependencies.md).

## Do I Need Docker?

Yes for the supported guided production path: Docker Engine with its local daemon and Compose v2 2.24.4 or newer. Setup does not install host packages or configure Docker. Docker Desktop, remote contexts, and rootless Docker are outside the accepted production topology. See [prerequisites](02-Install-and-Dependencies.md#guided-production-installation).

## Can I Use My Existing Reverse Proxy?

Yes. Choose external mode to expose the app only on a host loopback port and use your host-native proxy for public HTTPS and authoritative forwarding. Managed mode uses bundled Caddy. External mode leaves TLS, Host/forwarded headers, and the trusted proxy hop with you; see [ingress modes](29-Wayfarerctl.md#choose-an-ingress-mode).

## Are Trips and Timelines Public by Default?

No. Both are private until you explicitly enable public sharing. Public Timeline settings also support a delay and Hidden Areas. Group sharing has its own membership/visibility rules. See [Timeline privacy](06-Timeline.md#private-vs-public), [Trip sharing](04-Trips.md#public-trip-sharing), and [Groups](05-Groups.md#privacy-and-visibility).

## Can I Change My Password or Enable 2FA?

Yes, through account management. Production passwords require at least 15 characters, uppercase, lowercase, a digit, and a non-alphanumeric character. Lost-password recovery is administrator-assisted; registration does not collect email for automatic recovery. See [password basics](01-Getting-Started.md#production-password-policy) and [account recovery](29-Wayfarerctl.md#diagnosis-logs-and-user-recovery).

## Does Wayfarer Support Backup, Update, and Restore?

Yes. `wayfarerctl` supports opt-in Compose recovery sets, managed forward update, and restore against an independently trusted exact target. Backups pair the database with uploads and the complete active key ring; caches are rebuildable. Configure recovery before updating and follow the plan/authorization steps. See [backups](29-Wayfarerctl.md#compose-recovery-sets), [updates](29-Wayfarerctl.md#public-stable-acquisition-and-update-planning), and [restore](29-Wayfarerctl.md#managed-restore).

## What Is the Relationship with WayfarerMobile?

[WayfarerMobile](https://github.com/stef-k/WayfarerMobile) is the separate companion app. It connects to your instance using an API token for GPS tracking, check-ins, groups, and Trip navigation. Local history, pending delivery, downloaded Trips, and cached tiles have distinct roles. See [Mobile App](08-Mobile.md) for published-build behavior and offline limits.

## What Formats Can I Import and Export?

Import location history from Google Timeline JSON, Wayfarer GeoJSON, CSV, GPX, and location-history KML; generic GeoJSON is not a location-history upload format. Export history to GeoJSON, CSV, GPX, or KML. Trips import Google MyMaps/Wayfarer KML and export PDF or KML. See [Importing & Exporting](07-Importing-Exporting.md) for metadata and compatibility details.

## How Do I Join a Group?

Accept an invitation from its owner or manager in Wayfarer or WayfarerMobile. See [group invitations](05-Groups.md#invitation-system).

## Can I Embed Public Content on My Website?

Yes. Timeline Settings and the public Trip sharing menu offer **Copy embed URL** and **Copy embed HTML**. Open Wayfarer at its public HTTPS address, paste the iframe into your website, and adjust its height if needed. Ordinary wheel/single-finger input scrolls the page; Ctrl/Cmd + wheel and two fingers interact with the map. **Open full view** opens normal map navigation in a new tab. See [Timeline embedding](06-Timeline.md#embed-your-public-timeline) and [Trip sharing](04-Trips.md#public-trip-sharing).

## Does Wayfarer Send Anything to External Providers?

Your authoritative history stays on your server, but configured features can make external requests. Geocoding/routing providers receive necessary coordinates, searches, or route inputs; tile services receive tile requests. Personal provider credentials remain server-side and are never sent to WayfarerMobile. The mobile basemap also contacts OpenStreetMap directly. See [Personal Location Providers](24-Personal-Location-Providers.md) and [mobile privacy](08-Mobile.md#privacy-security).

## Can wayfarerctl Migrate an Old Native/systemd Installation?

No. Ordinary `wayfarerctl` does not implement native migration, and `update` is not a native-to-Compose migration tool. [#604](https://github.com/stef-k/Wayfarer/issues/604) is the maintainer's deferred production migration/cutover work. See the [installation boundary](02-Install-and-Dependencies.md#availability) and [operator guide](29-Wayfarerctl.md).

## Is Uninstall Supported?

There is no ordinary `wayfarerctl` uninstall command. `stop` retains installation state and volumes. Keep a verified recovery set and consult the [durable-state boundary](29-Wayfarerctl.md#secrets-and-durable-state) before planning any separate administrative removal.
