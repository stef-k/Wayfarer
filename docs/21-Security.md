# Security

Identity & Roles
- ASP.NET Core Identity with roles: `Admin`, `Manager`, `User`.
- Registration GET and POST require an explicitly open `ApplicationSettings` row. Closed or missing settings redirect to `/Home/RegistrationClosed` before account lookup, creation, role assignment or API-token creation. Open registration retains its existing User role, incoming-location token row and username-based confirmation redirect.
- Usernames are the unique login identifier. Custom registration does not collect email; packaged Identity recovery/confirmation pages remain reachable, with no-op email delivery. The existing username/email confirmation mismatch is unchanged.

Passwords
- Never commit or document real passwords. For local dev, use throwaway credentials and rotate.
- Admin seeding creates a protected admin account; change credentials immediately after first run.
- Policy: minimum eight characters with at least one upper-case letter, one lower-case letter, one digit, and one special character.

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
- Avoid logging sensitive PII; mask or omit where possible.

Secrets Management
- Use environment variables or user‑secrets in development; use secret managers/VAULTs in production.


Two-Factor Authentication (2FA)
- Identity area includes Enable Authenticator pages for TOTP-based 2FA.
- Encourage users to enable 2FA, especially for admin accounts.

CSRF Protection
- All admin endpoints are protected with anti-forgery tokens.
- AJAX calls to sensitive endpoints (cache deletion, settings changes) include CSRF tokens.
- Forms use `@Html.AntiForgeryToken()` and controllers validate with `[ValidateAntiForgeryToken]`.

Rate Limiting
- **Tile requests** — Anonymous users limited to 500 requests/minute per IP (configurable).
- **Check-in endpoint** — Rate-limited to prevent spam (default: 10 second cooldown).
- **Location logging** — Filtered by time and distance thresholds.
- Rate limit headers included in API responses.

XSS Prevention
- Tile provider attribution is sanitized using HtmlSanitizer before rendering.
- User-generated content (notes, names) escaped in views.
- Rich HTML content (trip notes) rendered in controlled contexts.

IP Address Handling
- X-Forwarded-For header trusted only from localhost and private IP ranges.
- Prevents IP spoofing attacks when behind reverse proxies.
- Configure trusted proxies in `Program.cs` for your deployment environment.

Uploads & Secrets
- Do not store tokens or secrets in exports or logs.
- Avoid logging PII. Use role-based checks on admin endpoints.
- API keys are redacted from tile service logs to prevent exposure.

Persistent reverse geocoding never exposes credentials to callers. Provider URLs, coordinates, addresses, and payloads are excluded from application diagnostics; provider exceptions are translated to bounded categories inside the shared boundary. Mapbox Permanent contact requires explicit current consent, authorization, verification, selection, and meter admission.

Optional provider-returned feature names and types follow the same visibility rules as their Location or Trip Place. They are encoded for presentation and are not emitted through logs, diagnostics, audit errors, SSE, or job history.

Import/enrichment commands derive ownership only from authenticated `NameIdentifier` and require antiforgery validation. Their SSE endpoint derives its channel from that claim; the anonymous generic stream rejects all `import` and `enrichment` prefixes. Content-free events are reload hints only. Filenames, per-Location timestamps, coordinates, addresses, credentials, provider URLs/payloads, stack traces, filesystem paths, and raw exceptions are not emitted or retained as workflow errors.

Per-user group notifications follow the same claim-owned boundary at `/api/sse/group-notifications`.
The endpoint accepts no user or channel identifier, subscribes only to `group-notifications-{NameIdentifier}`,
and permits only exact content-free `invitation-state` and `membership-state` hints. Generic invitation and
membership notification prefixes are rejected case-insensitively. These hints never contain user, group,
invitation, action, or presentation fields; clients reload authenticated durable state. This restriction does
not alter the detailed events available on membership-authorized `/api/sse/group/{groupId}` streams.
