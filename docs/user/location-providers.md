---
title: Personal location providers
---

# Personal location providers

Configure an optional provider when you want Wayfarer to fill missing addresses,
search for Trip Places or generate route previews. This guide is for the User
account choosing and paying for its own provider use. You can record locations,
import history and plan manually with **No provider** selected.

## What personal providers do

**Geocoding** turns coordinates into address information. **Directions** generates
routes between positions. They are independent choices: you can enable one,
both or neither. Your provider settings are personal, not the instance's map-tile
settings or the API token used to connect Mobile.

| Provider | Current Wayfarer features |
| --- | --- |
| **Geoapify** | Retained address enrichment, submitted Trip Place search and directions using supported modes. |
| **Mapbox Permanent Geocoding** | Retained address enrichment with explicit Permanent consent. Mapbox Directions is not implemented. |

## Privacy and credentials

Provider requests leave Wayfarer. Address lookup sends coordinates; Trip search
sends your search query; directions sends the route's positions
and chosen mode. The provider and its service infrastructure receive the
information needed for that request. Hidden Areas affect public Timeline display,
not whether a location is sent for an enabled address lookup.

Wayfarer protects stored credentials and displays only a mask after saving them.
The saved key is not returned to your browser or sent to WayfarerMobile. Hosted
Mobile routing goes through your authenticated Wayfarer server. Treat the initial
key entry and your provider account as secrets, and use a dedicated Wayfarer key
where possible.

Provider privacy and billing terms still apply. For example, Geoapify describes
request information and retention in its [privacy policy](https://www.geoapify.com/privacy-policy/).
Choosing no personal provider does not stop all external contact: Trip search can
use public Nominatim, maps use tile services, and external navigation or links
contact their own services.

## Configure a provider

Open **Settings > Personal Location Providers** in the web app. The page shows
credential status, geocoding and directions choices, verification state and usage.

1. Create a key/token in the provider account and save it under that provider.
2. Review its Wayfarer usage guard and the provider's actual account limits.
3. Complete the provider-specific consent and verification steps below.
4. Select the verified provider under **Geocoding** or **Directions** and save that
   choice. Verification alone does not select it.
5. Check that the capability's state reports it ready before requesting work.

Verification makes one provider request using fixed non-personal test coordinates
or route inputs. It consumes admitted usage and, for paid products, can be billed.
Opening the settings page alone does not run verification.

Replacing a credential can leave saved provider choices blocked until you verify
them again. Geoapify replacement disables both capabilities; reverify and select
each one you need. Mapbox replacement clears Permanent consent and verification,
so repeat those steps. Switching providers does not erase the inactive profile or
reset usage. **No provider** stops that capability; follow any requested
verification steps before enabling it again.

## Geoapify

Create a key through [Geoapify's account portal](https://myprojects.geoapify.com/).
Review its [current plans](https://www.geoapify.com/pricing/) rather than assuming
the Wayfarer guard is the provider's billing allowance.

### Geocoding

Save the Geoapify credential, choose **Verify Geoapify geocoding**, then select
**Geoapify** under **Geocoding** and choose **Save geocoding provider**. Verification
authorizes this capability but leaves selection to you.

Successful lookups can add address fields and, when returned, separate mapped
feature context. That context is not a visit record. Existing saved addresses
remain readable if you later switch providers. To process older missing addresses,
use the [enrichment controls](#address-enrichment-controls).

### Trip search

The web Trip editor sends a search only when you submit it. A ready selected
Geoapify geocoding configuration supplies attributed results while its guard has
capacity. Searches share its geocoding/routing allowance.

With no geocoding provider selected, Mapbox selected, or Geoapify's allowance
exhausted, search can use attributed public Nominatim instead. Do not submit
confidential or personal information: this is still an external search. If an
active Geoapify key is broken, revoked or unverified, or a contacted Geoapify
request fails, Wayfarer reports the problem rather than sending the same search
to another provider. Enter a Place manually when search is unavailable.

### Directions and routing

Choose **Verify Geoapify directions**, select **Geoapify** under **Directions**,
then choose **Save directions provider**. Geocoding verification does not verify
directions.

Current modes are **Walk**, **Bicycle**, **Motorcycle**, **Drive** and **Bus**. Choose
one explicitly for a web route preview or from Mobile's offered directions choices.
A Segment's transport profile is a separate planning choice and does not imply a
provider routing mode.

### Usage guard

Geoapify geocoding, Trip search and routing share one Wayfarer credit pool. Its
enabled default guard is **2,500 credits in a rolling 24-hour window**. Capacity
returns as older counted requests leave that window, rather than at a fixed daily
reset time.

Wayfarer admits one credit for a geocoding or uncached search request. For routing,
it reserves one credit per consecutive waypoint pair for Walk/Bicycle and a
conservative 21 per pair for Motorcycle/Drive/Bus. Those are Wayfarer's safety
counts, not an exact invoice forecast. Verification, admitted failures and retries
can consume usage; cached or saved-result reuse makes no new provider request.

Set a limit you are comfortable with and leave the guard enabled. Wayfarer cannot
see provider usage outside this instance, and several keys may share one account
allowance. Disabling the guard can permit paid usage or provider suspension.

## Mapbox Permanent Geocoding

Wayfarer retains enriched addresses. Mapbox **Temporary** results cannot be cached;
**Permanent** results can be retained. Therefore Wayfarer uses Permanent Geocoding
with explicit consent for new retained Mapbox enrichment. Mapbox requires an
eligible credit card or enterprise contract for this storage mode; see its
[storage requirements](https://docs.mapbox.com/api/search/geocoding/#storing-geocoding-results).

Permanent Geocoding is separately billed. Do not assume Temporary Geocoding's
free allowance covers it. Review [Mapbox pricing](https://www.mapbox.com/pricing/)
and your account terms before verification or bulk enrichment.

1. Save the Mapbox credential.
2. Read and acknowledge the Permanent storage, billing eligibility, possible
   charges and limits of Wayfarer's meter, then choose **Record Permanent consent**.
3. Choose **Verify Mapbox Permanent Geocoding**. This makes one potentially billable
   Permanent request at fixed non-personal coordinates.
4. Select **Mapbox Permanent Geocoding** under **Geocoding**, save and check readiness.
5. Keep its **Permanent Geocoding meter** enabled with an appropriate contact limit.

The meter uses a Wayfarer UTC calendar-month safety cycle, not a promise about your
provider's billing reset. Key replacement and provider switching do not reset
usage. A Directions guard may be displayed separately; it does not make Mapbox
Directions available.

## Address enrichment controls

Open **Location imports > Missing-address enrichment** to inspect progress and
provider availability. You can opt in when [uploading history](import-export.md#optionally-fill-missing-addresses)
or start later. Geoapify's provider section also offers **Start backfill**.

Enrichment normally works through wholly missing addresses without overwriting
manual, imported or existing address fields. Progress is separate from import
completion and shares the provider's remaining guard capacity. Large histories
may take days.

Only meaningful controls are shown for the current state:

| Control | Result |
| --- | --- |
| **Start** | Enable processing of eligible missing-address work, including restarting after cancellation. |
| **Pause / Resume** | Suspend work, or continue a paused workflow. Resume does not restart a cancelled one. |
| **Cancel** | Stop the enrichment task; keep locations and addresses. An already sent request may still complete and count toward usage. |
| **Retry deferred** | Explicitly retry eligible no-result or attempt-limit records. It does not reset provider usage or successful results. |
| **Repair incomplete addresses** | Prepare a bounded set of eligible Geoapify addresses missing a city/place for another lookup; fill only blank fields. |

Incomplete addresses are not proof that the provider can supply more detail.
Manual and unknown-origin addresses are not automatic Geoapify repair candidates.
Start alone does not prepare incomplete-address repairs. Invalid-coordinate rows
cannot be fixed by retrying a provider; correct their coordinates separately.

Temporary errors may retry later while work remains enabled. No result or repeated
failed attempts can require an explicit retry. If a repair returns no useful
locality, the old address remains and there is no automatic repeat of that outcome.
You can also [correct an address manually](timeline.md#add-or-correct-a-location).

## Review and save generated routes

In a Trip Segment, choose a supported directions mode and **Generate routed path**.
Review the preview and estimates, then **Save Segment** to commit it. **Discard
proposal** leaves the previous route unchanged. See [Trip route editing](trips.md#preview-a-provider-generated-route)
for duration choices and the rest of the planning workflow.

Saved provider routes remain usable after a provider switch, key replacement or
outage. Retained results keep their provider attribution. Mobile can retain
matching validated guidance locally; a one-off Mobile directions request does not
create a saved Trip Segment on the server. See [Mobile routing](mobile.md#hosted-routing-and-direct-guidance).

## When a limit or provider blocks new work

Wayfarer pauses new requests if the guard is exhausted, a credential is unusable,
verification is missing or the provider cannot serve the request. Existing
addresses, history and saved routes are not erased. Capture, accepted imports,
exports, synchronization and manual planning continue without new enrichment.

Capacity can return as rolling credits expire or a monthly cycle advances.
Enabled enrichment can then continue; cancelled work still needs an explicit
start. Raising or disabling a guard is a cost decision, not a necessary repair.
An unavailable routing request does not replace saved geometry or silently use
another routing provider.

## Resolve common setup problems

- **Verified but unavailable:** check that you selected and saved the provider for
  the intended capability. Verify geocoding and directions separately.
- **Unavailable after replacing a key:** repeat verification and selection;
  Mapbox also needs fresh Permanent consent.
- **Usage full:** check the window/cycle and remaining capacity, then wait or
  deliberately change the limit after reviewing provider billing.
- **No address or incomplete address:** inspect enrichment status. Retry only
  eligible deferred work, prepare supported repairs or enter the address manually.
- **Credential cannot be read:** ask the administrator to investigate server
  credential recovery. Do not paste keys into support messages or diagnostics.
- **Suspected key exposure:** revoke it in Wayfarer and in the provider account,
  then configure a replacement if needed. Revocation preserves saved data.

Server backups and protected-credential recovery belong to the
[wayfarerctl reference](../self-hosting/wayfarerctl.md), not personal provider setup.

[User guide](index.md) · [Documentation home](../README.md)
