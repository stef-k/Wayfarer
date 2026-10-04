# Security

Identity & Roles
- ASP.NET Core Identity with roles: `Admin`, `Manager`, `User`.
- Registration GET and POST require an explicitly open `ApplicationSettings` row. Closed or missing settings redirect to `/Home/RegistrationClosed` before account lookup, creation, role assignment or API-token creation. Open registration retains its existing User role, incoming-location token row and username-based confirmation redirect.
- Usernames are the unique login identifier. Custom registration does not collect email; packaged Identity recovery/confirmation pages remain reachable, with no-op email delivery. The existing username/email confirmation mismatch is unchanged.

Passwords
- Never commit or document real passwords. For local dev, use throwaway credentials and rotate.
- Production policy: minimum **15 characters**, including at least one uppercase letter, one lowercase letter, one digit, and one non-alphanumeric character.
- Production startup does not create or reset the administrator. Explicit bootstrap establishes the protected administrator; [guided Compose setup](02-Install-and-Dependencies.md#guided-production-installation) supplies the administrator password before installation is considered ready.
- Development uses a separate local password/seed policy with throwaway credentials.

Account Lockout
- Accounts are locked after 5 failed login attempts to protect against brute-force attacks.
- Lockout duration: 15 minutes.
- Applies to all users including new accounts.

Identity Attempt Admission
- One application-owned, process-local budget admits **20 attempts per effective client per five-minute fixed window**, shared across usernames and the selected Identity handlers. This leaves room for password retries plus 2FA/recovery while limiting cross-account spraying; the five-failure account lockout remains complementary.
- Admission covers POST Login, Register, LoginWith2fa, LoginWithRecoveryCode, ForgotPassword, ResetPassword and ResendEmailConfirmation. It also covers GET/HEAD ConfirmEmail with userId/code, ConfirmEmailChange with userId/email/code, and RegisterConfirmation with email. Named-handler fallback uses the same budget.
- Ordinary form/status GETs, authenticated Manage pages, external-login pages (no providers configured), and `/api/**` are excluded. Reinventory external login if providers are added. Mobile bearer authentication, SSE and token import do not use this component.
- Only post-forwarding `RemoteIpAddress` identifies the client; mapped IPv4 addresses share the IPv4 budget. Missing addresses are rejected. No forwarding header is parsed by admission.
- At most **4,096 live client buckets** are stored. Expired entries are removed on subsequent attempts; a full store rejects new clients until expiry rather than evicting live budgets. Existing clients retain their remaining budget. All storage and cleanup work is bounded.
- Rejections return **429** with whole-second `Retry-After` (remaining window, earliest expiry at capacity, or five minutes for a missing address). Admission runs after authorization/antiforgery and before model binding and handlers. Admitted Identity messages, redirects and account lockout are unchanged.
- Clients behind one NAT share a budget. Windows begin on the first admitted attempt and reset after five minutes; process restart resets this in-memory state. It is not distributed admission for multiple application instances.

API Tokens
- **Wayfarer API tokens** (used for mobile app and API authentication) are stored as SHA-256 hashes—never in plain text. If the database is compromised, the tokens cannot be recovered or reused.
- Tokens are shown **only once** when created or regenerated. Users must copy and store them securely.
- **Personal provider credentials** are protected with purpose-, provider-, and user-bound Data Protection and are never redisplayed or sent to mobile. Key-ring backup, filesystem protection, privacy disclosure, and legacy migration are documented in [Personal Location Providers](24-Personal-Location-Providers.md).
- Mobile routing discovery exposes only provider-neutral profile metadata and an opaque equality-only `DiscoveryCatalogIdentity`; credential readability may affect membership but credential material never enters the identity. Capability exchanges that identity for an equality-only `SelectedProfileAuthorityIdentity` containing non-secret selected execution authority, including native mode and relevant generations. Neither identity is authentication or authorization proof. Neither contains credentials, protected endpoints, or quota state, and stale-identity failures make no provider contact and emit only bounded outcomes.
- **Geoapify query authentication** stays inside diagnostic-suppressed clients. Credentials, authenticated URLs, coordinates, addresses, geometry, instructions, and raw payloads are excluded from logs and DTOs; adapters translate transport exceptions to bounded product categories.
- Rotate API tokens regularly and revoke any that may have been exposed.

Authorization
- Admin/Manager areas require roles.
- API endpoints enforce ownership or public flags for trips and group membership for timelines.

Headers & Proxies
- Configure forwarded headers and trusted networks in `Program.cs` for your reverse proxy.

Data Privacy
- Self‑hosted: operators are responsible for retention, backups, and legal compliance.
- API unhandled failures preserve the stable 500 envelope with fixed generic `details` and `requestId = HttpContext.TraceIdentifier`. Known group/invitation/backfill rules remain actionable; unrelated failures never become public exception messages. Public timeline and cache-purge SSE failures also use bounded text.
- Routine diagnostics retain exception types, request/user/resource IDs, counts and fixed outcomes. They omit image-origin URLs/queries, coordinates, GPS/movement values, private trip/place names, visit timestamps and imported XML/tag content. The image-origin typed client suppresses factory HTTP logging as well as unsafe explicit diagnostics.
- Generic MVC error audits contain bounded exception type and request identity, not exception messages. Real storage initialization and filesystem failures retain repair-relevant paths; routine thumbnail generation and tile hits/writes use resource identity. Historical audit rows are unchanged.

Secrets Management
- Use environment variables or user‑secrets in development; use secret managers/VAULTs in production.


Two-Factor Authentication (2FA)
- Identity area includes Enable Authenticator pages for TOTP-based 2FA.
- Encourage users to enable 2FA, especially for admin accounts.

CSRF Protection
- Unsafe Admin mutation POST actions validate antiforgery; read-only navigation does not require it.
- Protected cookie-authenticated browser mutations require a server-issued request token plus its antiforgery cookie. Forms use `__RequestVerificationToken`; JSON and bodyless callers send `RequestVerificationToken` from the page's hidden token.
- Trip import sends the header alongside its unchanged multipart body. Header validation avoids an additional early antiforgery form read on valid requests; it does not prevent upload ingress or buffering. The upload middleware applies the effective request cap before MVC antiforgery/model binding (#662).
- `PUT /api/Location/{id:int}` preserves cookie-first authority: an authenticated cookie always requires its antiforgery token, regardless of any bearer header. Only a successfully resolved bearer without selected cookie authority proceeds token-free. Inactive/missing cookie accounts do not fall through to bearer.
- Bearer-only APIs retain their separate credential authority and do not require MVC antiforgery tokens. Read/query POSTs remain token-free where appropriate; no global MVC antiforgery policy is installed.
- Identity/Razor Pages retain framework automatic antiforgery. Admin Jobs forms rebuilt by SSE copy the authentic page token.
- Provider-settings navigation is read-only. Settings-owned legacy Mapbox migration requires an authenticated User-role, antiforgery-protected POST with ownership derived only from the identity claim (#679). Existing provider-contact/background preparation may still converge legacy state internally under its own authority; see [the migration lifecycle](24-Personal-Location-Providers.md#legacy-mapbox-migration).
- The unintended unconstrained Trip clone alias was removed; the bearer-only `POST /api/trips/{id}/clone` contract is unchanged.

Rate Limiting
- Baseline Identity/API/location-ingestion admission and rate protections are application-owned. Optional host or proxy rate limiting is defense in depth.
- **Tile requests** — Anonymous users limited to 500 requests/minute per IP (configurable).
- **Incoming API token lookups** — Immediate, nonqueued active-work admission: 32 globally / 16 per effective client.
- **Location ingestion** — Immediate, nonqueued active-work admission: 64 globally / 8 per authenticated user, shared by LogLocation and CheckIn. Persisted idempotent replay resolves before new ingestion admission.
- New overloaded work returns 429 for client/user saturation or 503 for global saturation, with the existing `Retry-After` semantics, preserving mobile retry compatibility. These are concurrent-work ceilings, not requests-per-minute quotas.
- Time/distance recording thresholds remain product filtering; CheckIn has no 10-second cooldown.

XSS Prevention
- Tile provider attribution is sanitized using HtmlSanitizer before rendering.
- User-generated content (notes, names) escaped in views.
- Rich HTML content (trip notes) rendered in controlled contexts.

IP Address Handling
- ASP.NET Forwarded Headers configuration owns proxy trust: only explicitly configured known proxies/networks are trusted. Private or loopback addresses alone confer no forwarding trust.
- The supported managed Caddy peer and the documented external-proxy trusted peer are the intended authorities; configure them through `TrustedProxy` as described in [Compose deployment](28-Production-Compose.md).
- Downstream admission, rate limiting and logging consume the post-forwarding normalized `RemoteIpAddress`; they do not reparse hostile `X-Forwarded-For` headers.

Uploads & Secrets
- The fixed application request ceiling is 100 MiB. Only Location-history and Trip KML uploads consume the Admin `-1 / 0 / 1..100 MiB` policy, after authorization and before multipart buffering. Trip KML adds its existing parser-derived section budget; see [configuration](16-Configuration.md). Managed Caddy does not duplicate this policy.
- Do not store tokens or secrets in exports or logs.
- Avoid logging PII. Use role-based checks on admin endpoints.
- API keys are redacted from tile service logs to prevent exposure.

Persistent reverse geocoding never exposes credentials to callers. Provider URLs, coordinates, addresses, and payloads are excluded from application diagnostics; provider exceptions are translated to bounded categories inside the shared boundary. Mapbox Permanent contact requires explicit current consent, authorization, verification, selection, and meter admission.

Optional provider-returned feature names and types follow the same visibility rules as their Location or Trip Place. They are encoded for presentation and are not emitted through logs, diagnostics, audit errors, SSE, or job history.

Import/enrichment commands derive ownership only from authenticated `NameIdentifier` and require antiforgery validation. Their dedicated SSE endpoint derives its channel from that claim. Content-free events are reload hints only. Filenames, per-Location timestamps, coordinates, addresses, credentials, provider URLs/payloads, stack traces, filesystem paths, and raw exceptions are not emitted or retained as workflow errors.

Per-user group notifications follow the same claim-owned boundary at `/api/sse/group-notifications`.
The endpoint accepts no user or channel identifier, subscribes only to `group-notifications-{NameIdentifier}`,
and permits only exact content-free `invitation-state` and `membership-state` hints. These hints never contain user, group,
invitation, action, or presentation fields; clients reload authenticated durable state. This restriction does
not alter the detailed events available on membership-authorized `/api/sse/group/{groupId}` streams.

## Generic SSE authorization

The anonymous generic stream admits only exact ordinal `location-update`. Persisted
public/live eligibility and its delivery lease remain authoritative. Import/enrichment,
group, group-notification, visit, Admin, PDF/progress and all other protected families
are reachable only through their dedicated owners. Alternate case, prefix or hyphen
aliases are not generic authorization mechanisms.

## Browser response headers

The application emits `Content-Security-Policy: frame-ancestors 'self'`,
`X-Frame-Options: SAMEORIGIN`, `X-Content-Type-Options: nosniff`, and
`Referrer-Policy: strict-origin-when-cross-origin` on ordinary responses, including
Identity, API, static assets, thumbnails, health, redirects and errors.

Only successful public HTML embeds at `/Public/Trips/{id}?embed=true` and
`/Public/Users/Timeline/{username}/embed` use `frame-ancestors *` and omit XFO.
The controllers must first confirm public eligibility; missing/private resources and
failed renders retain the restrictive policy. Normal public Trip/Timeline pages do too.
Same-origin framing remains supported for export download fallbacks.

This CSP intentionally contains only the framing directive. Embedding requires no CORS
policy. The referrer policy preserves full same-origin Referer for tile abuse checks,
limits cross-origin disclosure to the origin, and suppresses HTTPS-to-HTTP referrers.
Image proxy, bearer API and local mobile WebView contracts are unchanged.
Proxies must pass these application headers through without adding conflicting values.
