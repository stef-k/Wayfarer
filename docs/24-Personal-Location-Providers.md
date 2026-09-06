# Personal Location Providers

Wayfarer stores one personal credential per user and provider (`Geoapify` or `Mapbox`). Credentials are protected with ASP.NET Core Data Protection and cryptographically bound to credential type, provider, and user. Browser and mobile responses show only a fixed mask; WayfarerMobile never receives provider credentials.

## Key-ring durability and backup

The supported Linux/systemd deployment pins `WorkingDirectory=/var/www/wayfarer` and `HOME=/home/wayfarer`. Its existing ASP.NET Core ring is `/home/wayfarer/.aspnet/DataProtection-Keys`; Wayfarer configures that retained path explicitly. The application discriminator remains the ASP.NET Core hosted discriminator derived from the fixed `/var/www/wayfarer` content root, preserving ciphertext created before personal-provider profiles were introduced. The ring survives process restarts and publish replacement without relocating existing keys.

The installer and deployer assign the ring to the `wayfarer` service account and set the ring directory to mode `0700`. They do not apply a separate mode to individual key files; the directory boundary prevents access by other accounts. At-rest protection is the dedicated service identity plus host filesystem permissions and disk/host encryption.

Back up the PostgreSQL database and key ring together in the same recovery set. Restore both before starting the application, then restore the production ownership and directory permission exactly:

```bash
sudo chown -R wayfarer:wayfarer /home/wayfarer/.aspnet/DataProtection-Keys
sudo chmod 700 /home/wayfarer/.aspnet/DataProtection-Keys
```

Losing the applicable key ring makes protected personal-provider credentials unreadable even when the database survives. Startup fails closed if the directory is unusable or retained protected credentials cannot be read.

This compatibility contract covers the fixed single-host systemd deployment at `/var/www/wayfarer`. Containers, a changed content root, and multiple hosts are not covered automatically and require an explicitly shared, stable Data Protection authority before deployment; Wayfarer does not claim certificate, cloud-KMS, container, or multi-host key sharing.

## Profiles, authorization, and switching

Geocoding and routing authorization, verification, and active selection are independent. “No provider” is supported. A replacement advances the credential generation and invalidates both verifications without changing authorization or usage. Revocation removes ciphertext, disables both capabilities, and preserves usage and all Locations, Timeline records, Places, Trips, Segments, addresses, enrichment, geometry, and accepted routes. Switching changes selection only: inactive profiles, credentials, verification history, guards, and usage remain retained.

Provider contact requires the active selection, an authorized and currently verified capability, readable current-generation credential, and usage admission. Replacement, revocation, disabling, or relevant switching invalidates stale in-flight authority before contact or persistence. Provider adapters own HTTP, cost calculation, payload parsing, normalization, retries, and domain persistence.

## Legacy Mapbox migration

On the authenticated user’s provider-settings entry and common geocoding resolver, Wayfarer recognizes only trimmed, case-insensitive exact `Mapbox` names. It never performs a startup-wide scan. One unambiguous value is protected, read back through production Data Protection, compared in memory, and only then are exact matching legacy rows retired. Generic geocoding is authorized for compatibility; routing is not. Migration never grants Permanent consent or selects Mapbox, so migrated profiles are configured but paused, unverified, and inactive. Maintainer-managed family accounts must be migrated explicitly after the compatible backend is deployed.

## Mapbox Permanent Geocoding

Mapbox Geocoding v6 defaults to Temporary mode. Temporary results may not be cached; retained Wayfarer enrichment therefore uses only explicitly consented Mapbox Permanent Geocoding with `permanent=true`, or makes no Mapbox contact. Permanent results may be stored indefinitely, are separately billed with no advertised free tier, and require an eligible credit card or active enterprise contract.

The settings workflow is deliberately ordered: configure a masked credential, acknowledge storage and possible billing, authorize geocoding, run one explicit potentially billable verification at fixed non-personal coordinates, explicitly select Mapbox, and configure the separate Permanent contact meter. Verification does not activate Mapbox. Credential replacement, revocation, or disabling geocoding clears consent and verification; provider switching and meter changes preserve consent.

Wayfarer's meter counts only Wayfarer contacts. Other applications or tokens can consume the Mapbox account allowance. A disabled guard can incur charges. With no eligible provider, consent, verification, selection, or remaining budget, capture, imports, Trips, Places, Timeline, exports, and synchronization continue without new enrichment. Provider failures preserve submitted/manual and prior enrichment.

Historical rows have unknown nullable provenance because they may contain Temporary Mapbox output, imports, or manual edits; deployment does not delete or reclassify them. New successful Mapbox enrichment records `mapbox`, `permanent`, and its UTC persistence time. Address enrichment uses an explicitly opted-in relational workflow over bounded provider admission; provider changes never grant consent or silently start work.

## Geoapify persistent geocoding and routing

### Trip Editor place search

An explicit Trip Editor search uses the authenticated user's current active Geoapify geocoding authority. Each admitted provider contact consumes one credit from the existing shared Geoapify rolling allowance; a current 60-second authority-bound memory-cache hit consumes no credit. Wayfarer uses attributed public Nominatim only when no personal geocoding provider is selected, Mapbox is selected, or Geoapify is known or authoritatively found to be exhausted. A broken, revoked, unreadable, unauthorized, unverified, stale, or drifting active Geoapify authority fails closed, and a contacted Geoapify failure never falls back, so one submitted query reaches at most one provider.

The browser sends searches to Wayfarer as antiforgery-protected JSON POST requests, and provider HTTP clients suppress ordinary URI logging because upstream query strings contain the search and, for Geoapify, the key. Results retain linked attribution for the provider that actually supplied them and OpenStreetMap. Public Nominatim is best-effort, has no SLA, and its process-local one-request-per-second pacing is supported only for the documented single-host, single-process deployment; multiple active instances require an externally coordinated aggregate limiter or a different provider. Do not submit confidential or personal information to public Nominatim. Search remains submit-only with no typeahead, retry, or prefetch, and manual Place entry remains available.

Create a Geoapify account and a dedicated Wayfarer API key, then configure geocoding and directions as separate capability workflows. Each shows credential status, an explicit Verify action, one provider choice, and its ready or blocked state. Verify is the setup consent: it authorizes only that capability, consumes exactly one admitted fixed non-personal contact, records success for the resulting current generation, and never selects a provider. Choosing `No provider` revokes only that capability and makes its verification stale without deleting the credential. Replacing a credential disables both capabilities until each is explicitly verified and selected again. One protected key can serve both capabilities; it is never sent to the browser or WayfarerMobile.

Geoapify's Free plan was documented as 3,000 credits per 24 hours when retrieved on 2026-08-23. Wayfarer's enabled default is a conservative 2,500-credit rolling 24-hour safety window shared by geocoding and routing. Wayfarer cannot observe other account/key use or a provider reset timezone. Walk/bicycle routing admits one credit per consecutive waypoint pair; motorcycle/drive/bus conservatively admit 21 per pair. Every retry is admitted separately and admitted failures count. A disabled guard still records use and can risk paid usage or suspension.

Successful reverse geocoding stores normalized fields with `geoapify`, `persistent`, and UTC provenance. The explicit user action schedules a durable Quartz workflow that scans at most 10 owned wholly unenriched Locations chronologically per committed progress checkpoint; it never overwrites any manual/imported/existing field, stops on exhaustion, and resumes from still-unenriched domain state. The workflow stores bounded attempt authority rather than provider payloads or Location content in a queue.

When Geoapify's selected reverse-geocoding result includes a documented `name` and `result_type`, Wayfarer also retains that optional named-place context. It is displayed separately from the formatted address and user-authored Trip Place name. This does not perform nearby-place discovery or make an additional provider request; malformed optional values are ignored without rejecting a valid address.

Geoapify owns the closed directions catalog exposed by Wayfarer: Walk, Bicycle, Motorcycle, Drive, and Bus. Mapbox exposes no directions modes. Normal web requests require one explicit mode for every proposal and never infer it from a Segment Transport Profile. The planning profile, including a free-form profile, remains unchanged when a provider mode is selected.

Missing, unsupported, or stale modes fail before provider contact. Segments remain valid and saveable, manual or prior accepted geometry is preserved, and no alternate provider is contacted.

A pending proposal appears as a dashed cyan line over the current route. **Focus Active Entity** shows its extent; **Fit All** also includes it. **Proposed distance** (kilometres) and **Estimated travel time** (minutes) are labelled, bold estimates in External routed path. Missing estimates show **Unavailable**, while zero is valid. They do not update the ordinary fields before Save. An explicit Manual-duration override is disclosed and retained by Save. Discard removes the temporary line and estimates while preserving the previous route and other edits. Successful Save replaces the preview with the canonical route; a failed Save retains the proposal and error for explicit retry, discard or regeneration.

Generate and preview leave ordinary draft fields and stored data unchanged. Save Segment is the explicit acceptance and sole durable write of a pending Geoapify proposal together with other Segment edits; there is no separate Accept action. Discard proposal drops only that proposal. Save rechecks its original ten-minute protected context and current authority without provider contact. Stored geometry, distance, duration, normalized instructions, provider/native-mode and planning-profile provenance, generation time, attribution, and `persistent` authority remain usable after switching, key replacement, outage, or account closure under the terms retrieved 2026-08-23. Ad-hoc Mobile routes are returned but not stored by Wayfarer. Mobile may retain authorized, validated persistent routes locally for bounded matching and offline reuse. Display linked [Powered by Geoapify](https://www.geoapify.com/) and [© OpenStreetMap contributors](https://www.openstreetmap.org/copyright) with online and offline routed geometry.

Geoapify states that request data, headers, IP, and timestamps are used for access, usage, and statistics, and that successful-request data is generally retained no longer than 24 hours. Coordinates, routes, and addresses travel server-to-provider/CDNs. Wayfarer does not log credentials, authenticated URLs, coordinates, returned addresses, geometry, instructions, or raw payloads.

For a coordinated backend and Mobile rollout, back up PostgreSQL and the Data Protection key ring together and deploy the compatible backend before publishing the Mobile client. Restore both authorities together before starting the application, and configure family accounts explicitly only after deployment. No provider is selected automatically.

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

Processed, enriched, skipped, and failed-batch values are cumulative committed outcomes. Runnable, retryable-later, manual-retry, and invalid-coordinate counts are recomputed from current wholly-unenriched Locations; next attempt is the earliest future retry. Displayed provider credits come from the provider admission ledger, not an invented workflow counter.

Transient 429, timeout, network, and 503 outcomes use deterministic backoff and no more than three admitted attempts per provider generation. No-result and attempt-limit outcomes require an explicit **Retry deferred** action; invalid coordinates remain non-retryable. Attempts contain bounded identities, generations, outcomes, counts, and times only—never coordinates, addresses, credentials, URLs, payloads, or exception text.

Provider contact discloses coordinates and may disclose route inputs to the selected provider. Query-string authentication may be provider-required, but complete URIs, credentials, coordinates, returned addresses, request/response payloads, and imported content are excluded from Wayfarer logs and diagnostics. Revoke a provider key at both Wayfarer and the provider account when compromise is suspected; revocation does not delete historical data.
