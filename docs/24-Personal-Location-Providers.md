# Personal Location Providers

Wayfarer stores one personal credential per user and provider (`Geoapify` or `Mapbox`). Credentials are protected with ASP.NET Core Data Protection and cryptographically bound to credential type, provider, and user. Browser and mobile responses show only a fixed mask; WayfarerMobile never receives provider credentials.

## Key-ring durability and backup

The supported Linux/systemd deployment pins `WorkingDirectory=/var/www/wayfarer` and `HOME=/home/wayfarer`. Its existing ASP.NET Core ring is `/home/wayfarer/.aspnet/DataProtection-Keys`; Wayfarer configures that retained path explicitly. The application discriminator remains the ASP.NET Core hosted discriminator derived from the fixed `/var/www/wayfarer` content root, preserving ciphertext created before #499. The ring survives process restarts and publish replacement without relocating existing keys.

The installer and deployer assign the ring to the `wayfarer` service account and set the ring directory to mode `0700`. They do not apply a separate mode to individual key files; the directory boundary prevents access by other accounts. At-rest protection is the dedicated service identity plus host filesystem permissions and disk/host encryption.

Back up the PostgreSQL database and key ring together in the same recovery set. Restore both before starting the application, then restore the production ownership and directory permission exactly:

```bash
sudo chown -R wayfarer:wayfarer /home/wayfarer/.aspnet/DataProtection-Keys
sudo chmod 700 /home/wayfarer/.aspnet/DataProtection-Keys
```

Losing the applicable key ring makes protected administrator routing, personal routing, and location-provider credentials unreadable even when the database survives. Startup fails closed if the directory is unusable or retained protected credentials cannot be read.

This compatibility contract covers the fixed single-host systemd deployment at `/var/www/wayfarer`. Containers, a changed content root, and multiple hosts are not covered automatically and require an explicitly shared, stable Data Protection authority before deployment; Wayfarer does not claim certificate, cloud-KMS, container, or multi-host key sharing.

## Profiles, authorization, and switching

Geocoding and routing authorization, verification, and active selection are independent. “No provider” is supported. A replacement advances the credential generation and invalidates both verifications without changing authorization or usage. Revocation removes ciphertext, disables both capabilities, and preserves usage and all Locations, Timeline records, Places, Trips, Segments, addresses, enrichment, geometry, and accepted routes. Switching changes selection only: inactive profiles, credentials, verification history, guards, and usage remain retained.

Provider contact requires the active selection, an authorized and currently verified capability, readable current-generation credential, and usage admission. Replacement, revocation, disabling, or relevant switching invalidates stale in-flight authority before contact or persistence. Provider adapters own HTTP, cost calculation, payload parsing, normalization, retries, and domain persistence.

## Legacy Mapbox migration

On the authenticated user’s provider-settings entry and common geocoding resolver, Wayfarer recognizes only trimmed, case-insensitive exact `Mapbox` names. It never performs a startup-wide scan. One unambiguous value is protected, read back through production Data Protection, compared in memory, and only then are exact matching legacy rows retired. Generic geocoding is authorized for compatibility; routing is not. Migration never grants Permanent consent or selects Mapbox, so migrated profiles are configured but paused, unverified, and inactive. Under [#505](https://github.com/stef-k/Wayfarer/issues/505), maintainer-managed family accounts are migrated explicitly after the coordinated backend release.

## Mapbox Permanent Geocoding

Mapbox Geocoding v6 defaults to Temporary mode. Temporary results may not be cached; retained Wayfarer enrichment therefore uses only explicitly consented Mapbox Permanent Geocoding with `permanent=true`, or makes no Mapbox contact. Permanent results may be stored indefinitely, are separately billed with no advertised free tier, and require an eligible credit card or active enterprise contract.

The settings workflow is deliberately ordered: configure a masked credential, acknowledge storage and possible billing, authorize geocoding, run one explicit potentially billable verification at fixed non-personal coordinates, explicitly select Mapbox, and configure the separate Permanent contact meter. Verification does not activate Mapbox. Credential replacement, revocation, or disabling geocoding clears consent and verification; provider switching and meter changes preserve consent.

Wayfarer's meter counts only Wayfarer contacts. Other applications or tokens can consume the Mapbox account allowance. A disabled guard can incur charges. With no eligible provider, consent, verification, selection, or remaining budget, capture, imports, Trips, Places, Timeline, exports, and synchronization continue without new enrichment. Provider failures preserve submitted/manual and prior enrichment.

Historical rows have unknown nullable provenance because they may contain Temporary Mapbox output, imports, or manual edits; this release does not delete or reclassify them. New successful Mapbox enrichment records `mapbox`, `permanent`, and its UTC persistence time. Issue #507 adds an explicitly opted-in relational enrichment workflow over #502's bounded admission seam; provider changes never grant consent or silently start work.

## Geoapify persistent geocoding and routing

Create a Geoapify account and a dedicated Wayfarer API key, then use the ordered settings workflow: credential → geocoding authorization → geocoding verification → geocoding selection → routing authorization → routing verification → routing selection → shared guard/usage → optional backfill → revocation. Verification uses fixed non-personal requests, consumes one admitted credit per attempt, and never selects a provider. One protected key can serve both independently authorized capabilities; it is never sent to the browser or WayfarerMobile.

Geoapify's Free plan was documented as 3,000 credits per 24 hours when retrieved on 2026-08-23. Wayfarer's enabled default is a conservative 2,500-credit rolling 24-hour safety window shared by geocoding and routing. Wayfarer cannot observe other account/key use or a provider reset timezone. Walk/bicycle routing admits one credit per consecutive waypoint pair; motorcycle/drive/bus conservatively admit 21 per pair. Every retry is admitted separately and admitted failures count. A disabled guard still records use and can risk paid usage or suspension.

Successful reverse geocoding stores normalized fields with `geoapify`, `persistent`, and UTC provenance. The explicit user action schedules a durable Quartz workflow that scans at most 100 owned wholly unenriched Locations chronologically per execution; it never overwrites any manual/imported/existing field, stops on exhaustion, and resumes from still-unenriched domain state. The workflow stores bounded attempt authority rather than provider payloads or Location content in a queue.

Administrators enable a fixed-endpoint Geoapify routing configuration and map stable Wayfarer Transport Profile IDs with a closed dropdown: Not mapped, Walk, Bicycle, Motorcycle, Drive, or Bus. Display labels have no routing meaning: `WALK`, `walk`, `Walking`, localized labels, and custom labels are never matched automatically. Renaming a profile preserves its mapping; changing/removing a mapping advances configuration authority and invalidates stale proposals. Mapbox mappings remain separate under #500.

An unmapped profile shows “Route suggestions are not configured for this transport profile.” An invalid/stale mapping shows “This routing provider does not support the mapped transport mode.” Temporary provider/configuration failures show “Route suggestions are temporarily unavailable.” Segments remain valid and saveable, manual or prior accepted geometry is preserved, and no alternate provider is contacted.

Only explicit Trip Editor acceptance persists a Geoapify route. Stored geometry, distance, duration, normalized instructions, stable provider/configuration/profile/mapping provenance, generation time, attribution, and `persistent` authority remain usable after switching, key replacement, outage, or account closure under the terms retrieved 2026-08-23. Ad-hoc mobile routes are returned but not stored by Wayfarer; WayfarerMobile #253 owns bounded local matching and persistence. Display linked [Powered by Geoapify](https://www.geoapify.com/) and [© OpenStreetMap contributors](https://www.openstreetmap.org/copyright) with online and offline routed geometry.

Geoapify states that request data, headers, IP, and timestamps are used for access, usage, and statistics, and that successful-request data is generally retained no longer than 24 hours. Coordinates, routes, and addresses travel server-to-provider/CDNs. Wayfarer does not log credentials, authenticated URLs, coordinates, returned addresses, geometry, instructions, or raw payloads.

Issue #505 owns the coordinated release: #502 → #507 → #500, PostgreSQL and the Data Protection key ring are backed up together, backend deploys first, family accounts are configured explicitly after deployment, and Mobile publishes only after API/device acceptance. No provider is selected automatically and #502 must not be deployed publicly by itself.

Official policy sources retrieved 2026-08-23: [Geocoding v6 API and storage](https://docs.mapbox.com/api/search/geocoding/), [Temporary versus Permanent](https://docs.mapbox.com/help/dive-deeper/understand-temporary-vs-permanent-geocoding/), [pricing](https://www.mapbox.com/pricing/), and [attribution guidance](https://docs.mapbox.com/help/dive-deeper/attribution/).

Valid protected data always wins and is never overwritten. Matching duplicate casing rows converge; distinct values, invalid ciphertext, and revoked profiles preserve every recovery copy and fail closed without provider contact. Reruns are idempotent. Unrelated inbound Wayfarer API tokens and all domain data are untouched.

## Provider-native usage guards

Geoapify uses one shared user/profile pool for geocoding and routing. The default guard is enabled at 2,500 credits in a true rolling 24-hour Wayfarer safety window. PostgreSQL time and a locked pool row make multi-credit admission atomic across restarts and application instances; admitted failures count. Expired rows are removed under the same lock. Disabled guards still retain and clean the current rolling window so re-enabling does not reset it.

Mapbox Permanent Geocoding and Directions have separate counters, limits, exhaustion, and Wayfarer UTC calendar-month safety cycles. This is a configured Wayfarer boundary, not a claim about an unpublished provider reset timezone. Rotation and switching do not reset either product. One product’s exhaustion does not pause the other.

Wayfarer counts only contacts it admits. Cached/stored reuse and pre-HTTP rejection cost zero; admitted failures, timeouts, and admitted retries remain counted. Other applications or credentials may consume the provider account allowance. A dedicated Wayfarer key is recommended, but multiple keys do not necessarily create separate free allowances. Disabling a guard permits contacts beyond the configured safety limit and may incur paid usage.

## Exhaustion, imports, privacy, and recovery

Exhaustion stops new provider contact and recovers automatically as rolling credits expire, a product cycle advances, or a guard is raised/disabled. Source records remain retryable and historical data remains available. Imports and backfills use the same remaining pool and receive no catch-up burst.

## Resumable workflow authority

One retained workflow per user owns intent, epoch, state, progress, due time, an expiring execution lease with a monotonically advancing fence, and compact generation-bound attempts. Quartz owns one stable durable job and one current one-shot trigger; stale epochs no-op and startup reconciliation repairs scheduling metadata. The supported deployment runs one active scheduler because clustering is not configured. Wayfarer's relational lease/fence is product execution authority and short provider-ledger transactions remain admission authority; neither a database resource nor scheduler lock spans provider HTTP.

Processed, enriched, and skipped are cumulative committed outcomes. Retryable and permanent deferred counts describe committed attempt outcomes; failed batches count committed batch failures. Runnable remaining is recomputed from current wholly-unenriched, due eligibility, and next attempt is the earliest future retry. Displayed provider credits come from the provider admission ledger, not an invented workflow counter.

Transient 429, timeout, network, and 503 outcomes use deterministic backoff and no more than three admitted attempts per provider generation. Invalid coordinates and unusable results defer until **Retry deferred**. Attempts contain bounded identities, generations, outcomes, counts, and times only—never coordinates, addresses, credentials, URLs, payloads, or exception text.

Provider contact discloses coordinates and may disclose route inputs to the selected provider. Query-string authentication may be provider-required, but complete URIs, credentials, coordinates, returned addresses, request/response payloads, and imported content are excluded from Wayfarer logs and diagnostics. Revoke a provider key at both Wayfarer and the provider account when compromise is suspected; revocation does not delete historical data.
