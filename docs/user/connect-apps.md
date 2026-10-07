---
title: Connect apps to Wayfarer
---

# Connect apps to Wayfarer

## Connect an app to your account

WayfarerMobile, GPSLogger and compatible custom apps can send locations to your
Wayfarer account. You need a reachable HTTPS instance and an active **User** account.

An app uses a **connection token (API token)**, separate from your web password
and two-factor authentication. Give it only to trusted apps: it can also read and
change your data through the account's supported APIs. It is not a Geoapify or
Mapbox key; those belong in [Personal location providers](location-providers.md).

## Get a connection token

1. Sign in to the web app and open **Settings → Connect apps**.
2. Check the account and server address. The page initially shows safe status,
   without revealing or replacing a token.
3. If it says **No connection token**, choose **Create connection token**.
   Otherwise choose **Replace connection token**, then **Replace token and reconnect apps**.
4. While the new token is displayed, use **Copy token**, **Copy server address**,
   or scan the QR with Mobile. Connect every app that needs this shared credential.
5. Choose **Done — hide token** when finished. This clears the page's token and QR;
   your connected apps continue working.

**Replacing this token stops phones and other apps using it. Connect each one
again with the new token.** A fresh account usually already has a token whose
plaintext was not retained. Replace it explicitly to get your first usable copy.

Wayfarer stores only the new token's hash and shows plaintext once. Done, leaving,
refreshing or returning with Back cannot show it again. Another QR or readable
token requires another explicit replacement. Copying puts a copy on your device's
clipboard; keep that copy and the pairing QR private.

If a request fails or its response is lost, a new token may already be active and
the old one may have stopped working. Check the refreshed status; explicitly
replace again if you need a readable token. Do not repeatedly submit credential
requests. If a token is lost or exposed, replace it and reconnect your trusted apps.
Viewing Settings or this page does not invalidate existing clients.

If the page requires HTTPS, ask the instance operator to correct the instance or
proxy configuration. Use the actual HTTPS address rather than guessing a scheme
or copying an internal backend address.

## Connect WayfarerMobile

Keep the one-time reveal open while you scan its QR or copy the address/token into
the phone's setup wizard. See [Connect to your instance](mobile.md#connect-to-your-instance)
for the published app's scanner, manual setup and reconnection controls. The
[Mobile guide](mobile.md) also covers permissions, tracking, offline behavior and Trips.

## Use GPSLogger

Use **GPSLogger for Android by mendhak**, from [gpslogger.app](https://gpslogger.app/).
Its [Custom URL feature](https://gpslogger.app/#using-the-custom-url-feature)
allows a configurable method, JSON body and headers.

1. Get the connection token and server address as described above.
2. In GPSLogger's Custom URL settings, enable **Log to custom URL**.
3. Set the URL to your server address followed by `/api/location/log-location`,
   for example `https://wayfarer.example.com/api/location/log-location`.
4. Set the HTTP method to **POST** and leave Basic authentication fields empty.
5. Enter these HTTP headers on separate lines, replacing the placeholder with
   your connection token:

~~~text
Content-Type: application/json
Authorization: Bearer <connection-token>
~~~

Use this HTTP body template:

~~~json
{
  "latitude": %LAT,
  "longitude": %LON,
  "timestamp": "%TIME"
}
~~~

`%TIME` supplies ISO UTC time suitable for Wayfarer. Keep its quotes; latitude and
longitude are JSON numbers. Do not substitute epoch seconds for this timestamp.
The token goes in the Authorization header, never in the URL.

Optional measurements can be added when available: `accuracy` in metres (`%ACC`),
`altitude` in metres (`%ALT`), and `speed` in metres/second (`%SPD`). Omit unavailable
measurements. You may also supply `locationType`, `notes` or an existing `activityTypeId`.

Start logging, then check [Timeline](timeline.md) for a saved location. Both
GPSLogger's own capture settings and Wayfarer's server thresholds affect delivery.
GPSLogger can queue offline requests, but that does not guarantee every delayed
point will be saved or provide Wayfarer's optional idempotency/backoff strategy.

## Use another app

Apps that can send a custom HTTPS JSON request with an Authorization header may
be able to send locations to Wayfarer. Check the app's actual request capabilities;
this is not a claim of native support for every logger.

Use the same endpoint and headers as above with numeric `latitude` and `longitude`
and a capture `timestamp` such as `2026-10-07T12:00:00.000Z`. Coordinates use decimal
degrees within ±90 latitude and ±180 longitude; `(0, 0)` is rejected.

For an intentional check-in, send the same JSON shape to
`POST /api/location/check-in`. Check-in is distinct from automatic capture: it
bypasses normal automatic accuracy, time and distance filtering, while keeping
authentication, coordinate validation, capacity limits and persistence rules.
Do not configure an automatic logger as a check-in client to bypass capture policy.

## Check delivery and solve connection problems

- **HTTP success but no record:** automatic logging can return a successful result
  with `skipped: true`. Review capture thresholds in Settings and verify saved
  locations in Timeline. HTTP success alone is not proof that a point was saved.
- **Invalid credential:** check the HTTPS instance and the token retained by the
  app. Replacement invalidates the previous shared token; reconnect every app.
- **Disabled account:** contact the instance administrator. A disabled account
  cannot authenticate with a connection token.
- **Validation failure:** correct the JSON and fields before resending.
- **Temporarily busy:** custom clients should honor `Retry-After` on 429/503 and
  back off instead of repeatedly retrying. Authentication errors need correction.
- **Uncertain location write:** custom clients can use an optional GUID
  `Idempotency-Key`. Reuse a GUID only when retrying the same logical location;
  give each new location a new GUID. A malformed GUID returns 400; a persisted
  replay returns the previous success. The basic GPSLogger setup needs no such header.

Live app ingestion follows capture rules, including for delayed uploads. For
historical files and recovery, use [Import and export](import-export.md), which
has a separate workflow from live logging.

## Build a custom integration

Supported APIs include owned location history and corrections, owned Trip Regions
and Places, and permitted Mobile Group, visit, routing and live-update operations.
These enforce ownership and their own access rules; a token does not grant
unrestricted account administration or full Web editor parity.

See [API and SSE](../development/api.md) for endpoint contracts, response behavior,
authentication, retry rules and destructive-operation confirmation.

[User guide](index.md) · [Documentation home](../README.md)
