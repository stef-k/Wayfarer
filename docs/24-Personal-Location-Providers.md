# Personal Location Providers

Wayfarer stores one personal credential per user and provider (`Geoapify` or `Mapbox`). Credentials are protected with ASP.NET Core Data Protection and cryptographically bound to credential type, provider, and user. Browser and mobile responses show only a fixed mask; WayfarerMobile never receives provider credentials.

## Key-ring durability and backup

The supported Linux/systemd deployment already pins `HOME=/home/wayfarer`; its existing ASP.NET Core ring is `/home/wayfarer/.aspnet/DataProtection-Keys`. Wayfarer now configures that same path explicitly, and the installer/deployer enforces service ownership with mode `0700`. It survives process restarts and `/var/www/wayfarer` publish replacement without relocating existing keys. Keys are scoped to the application name `Wayfarer`; at-rest protection is the dedicated service identity plus host filesystem permissions and disk/host encryption. Wayfarer does not claim certificate, cloud-KMS, container, or multi-host key sharing.

Back up the key-ring directory together with the PostgreSQL database and restore both from the same recovery set. Losing applicable keys makes protected credentials unreadable. Startup fails closed if the directory is unusable or any retained administrator/personal routing or location-provider credential cannot be read.

## Profiles, authorization, and switching

Geocoding and routing authorization, verification, and active selection are independent. “No provider” is supported. A replacement advances the credential generation and invalidates both verifications without changing authorization or usage. Revocation removes ciphertext, disables both capabilities, and preserves usage and all Locations, Timeline records, Places, Trips, Segments, addresses, enrichment, geometry, and accepted routes. Switching changes selection only: inactive profiles, credentials, verification history, guards, and usage remain retained.

Provider contact requires the active selection, an authorized and currently verified capability, readable current-generation credential, and usage admission. Replacement, revocation, disabling, or relevant switching invalidates stale in-flight authority before contact or persistence. Provider adapters own HTTP, cost calculation, payload parsing, normalization, retries, and domain persistence.

## Legacy Mapbox migration

On the authenticated user’s provider-settings entry and common geocoding resolver, Wayfarer recognizes only trimmed, case-insensitive exact `Mapbox` names. It never performs a startup-wide scan. One unambiguous value is protected, read back through production Data Protection, compared in memory, and only then are exact matching legacy rows retired. Geocoding is authorized; routing is not.

Valid protected data always wins and is never overwritten. Matching duplicate casing rows converge; distinct values, invalid ciphertext, and revoked profiles preserve every recovery copy and fail closed without provider contact. Reruns are idempotent. Unrelated inbound Wayfarer API tokens and all domain data are untouched.

## Provider-native usage guards

Geoapify uses one shared user/profile pool for geocoding and routing. The default guard is enabled at 2,500 credits in a true rolling 24-hour Wayfarer safety window. PostgreSQL time and a locked pool row make multi-credit admission atomic across restarts and application instances; admitted failures count. Expired rows are removed under the same lock. Disabled guards still retain and clean the current rolling window so re-enabling does not reset it.

Mapbox Permanent Geocoding and Directions have separate counters, limits, exhaustion, and Wayfarer UTC calendar-month safety cycles. This is a configured Wayfarer boundary, not a claim about an unpublished provider reset timezone. Rotation and switching do not reset either product. One product’s exhaustion does not pause the other.

Wayfarer counts only contacts it admits. Cached/stored reuse and pre-HTTP rejection cost zero; admitted failures, timeouts, and admitted retries remain counted. Other applications or credentials may consume the provider account allowance. A dedicated Wayfarer key is recommended, but multiple keys do not necessarily create separate free allowances. Disabling a guard permits contacts beyond the configured safety limit and may incur paid usage.

## Exhaustion, imports, privacy, and recovery

Exhaustion stops new provider contact and recovers automatically as rolling credits expire, a product cycle advances, or a guard is raised/disabled. Source records remain retryable and historical data remains available. Imports and backfills use the same remaining pool and receive no catch-up burst.

Provider contact discloses coordinates and may disclose route inputs to the selected provider. Query-string authentication may be provider-required, but complete URIs, credentials, coordinates, returned addresses, request/response payloads, and imported content are excluded from Wayfarer logs and diagnostics. Revoke a provider key at both Wayfarer and the provider account when compromise is suspected; revocation does not delete historical data.
