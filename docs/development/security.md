---
title: Developer security model
---

# Developer security model

This page describes security boundaries contributors must preserve in application code. Host hardening, TLS, backup, update and operator-secret procedures belong to the [self-hosting documentation](../self-hosting/index.md).

## Identity, roles and password behavior

Wayfarer uses ASP.NET Core Identity with `ApplicationUser` and the `Admin`, `Manager` and `User` role model. Authorization is not only a role check: private resources also require the owning user, group membership/management or another resource-specific relationship.

Production Identity mutations use the shared `IdentityPasswordPolicy`: at least 15 characters with upper/lowercase, digit and non-alphanumeric requirements. Development retains its existing development-seed compatibility. Account lockout is enabled for new users: five failed attempts produce a 15-minute lockout.

Selected Identity credential/recovery/token routes also use application-owned attempt admission. Keep that boundary separate from general `/api` token admission.

## Cookie Trip sharing mutations (#907)

`PATCH /api/trips/{tripId}/editor/share-progress` and
`POST /User/Trip/ToggleShareProgress` use `TripShareProgressMutationService`.
A previously issued User-role cookie and valid antiforgery token provide HTTP
admission; the write also requires the current active account and current Trip
ownership. Both enable and disable use this authority boundary.

On PostgreSQL, one `READ COMMITTED` transaction locks the owned Trip `FOR UPDATE`,
then the active `AspNetUsers` row `FOR SHARE`, matching protected Trip metadata's
lock order. Untracked reads obtain post-wait values even when the request already
tracks a user or Trip. The account lock waits for pending deactivation and keeps
activity stable through commit. An account deactivation that starts after this
lock must wait for the sharing transaction to finish. Do not acquire these locks
in reverse order or replace this with an early activity lookup.

Enabling still requires current publication; an active owner may disable on a
private Trip. Only `ShareProgressEnabled` and `UpdatedAt` are written, preserving
a newer stored timestamp and unrelated metadata. Missing/foreign Trips remain
concealed: the editor returns 404, while the legacy action retains its generic
`success:false` JSON. Inactive accounts receive editor 401 or that same generic
legacy denial. Existing success envelopes and publication-validation errors stay
compatible. Commit/reply failures remain uncertain and provide no retry authority.

The routed PostgreSQL tests in `TripMetadataConditionalPostgresTests.Sharing.cs`
use real cookies/antiforgery and confirmed `pg_blocking_pids` waits, checking
persisted state from fresh contexts. Existing privacy-race tests cover both
sharing routes against protected metadata in either request order. InMemory tests
provide controller evidence only.

This correction covers both direct sharing toggles. Protected metadata already
checks active authority when privacy clears sharing. Legacy full metadata's
derived privacy clear and new-Trip creation retain their existing contracts;
they are outside this direct-toggle correction. This is not global cookie
revocation or a general Identity/legacy-policy redesign. The separate #853 C3
sharing unknown-outcome obligation, independent review and maintainer acceptance
remain open; this correction does not settle them.

## API-token authority

Current Wayfarer API tokens are cryptographically random, shown to the user once and stored as SHA-256 hashes. `IncomingApiTokenResolver` is request-owned: it admits and resolves a token once for the current HTTP request and never retains a positive identity across requests.

API-token lookup capacity is bounded globally/per effective client. Mobile endpoints use `IMobileCurrentUserAccessor`; general API helpers use the same underlying resolver. Extend these owners instead of adding a second bearer parser or token cache.

Inactive accounts do not authenticate through the bearer path.

The personal **Settings → Connect apps** surface manages only the canonical incoming
credential with User-role cookie Identity, an existing active owner and antiforgery.
GET exposes safe metadata. Explicit HTTPS Create/Replace returns plaintext directly
as JSON only after commit; replacement atomically matches the observed row ID and
issuance timestamp. New/replaced tokens persist only SHA-256 in `TokenHash`, with
`Token = null`. No Data Protection ciphertext, TempData, session, cookie or cache
retains bearer plaintext for recovery. Legacy extra administrative rows are not migrated.

Browser Done/navigation clears the active reveal and local QR, including BFCache
and late responses, without revocation. Another reveal needs explicit replacement;
an uncertain POST is never retried automatically. QR pairing uses the actual HTTPS
browser origin. Keep bearer authority separate from protected outbound provider
credentials and the existing key ring. [User setup](../user/connect-apps.md) owns
the shared connection instructions.

## Authorization and ownership

Apply authorization at the boundary that has enough context to decide it. `[Authorize]`/roles are appropriate for coarse HTTP admission; trip ownership, group membership, target-user rules and provider ownership usually require service/database state as well.

Do not accept a user ID, trip ID, group ID or provider profile ID from a request as proof of ownership. Resolve the authenticated caller independently and join the resource to that authority.

Installation Managers may administer an existing account only when its complete
persisted Identity-role set is exactly `{ User }`. The Manager user list/forms,
edits, deletion, password resets and named-token actions share this boundary.
Admin/Manager membership, any other multi-role set, unknown roles and roleless
accounts are excluded, including bootstrapped administrators with operator-chosen
usernames. Submitted roles, Group membership and UI visibility grant no authority.

Eligible inactive and protected Users retain their existing management operations;
protection prevents deletion until explicitly removed. Manager edits validate the
unchanged username and submitted `Role="User"` before assigning fields, leave role
membership intact and invalidate sessions after success. Rejected submissions must
leave account fields, roles, credentials and security stamps unchanged. Token owner
matching, duplicate-name handling, canonical deletion protection and secret-free
administrative presentation remain required. This rule does not change Admin role
editing, personal connection-token authority or bearer/Mobile contracts (#823).

Manager named-token Create, Regenerate and Delete own a short database transaction.
On PostgreSQL they lock the target `AspNetUsers` row before reading its complete
persisted roles, then retain the lock through the token write and commit. Explicit
`READ COMMITTED` gives the role query a fresh snapshot after a lock wait. The
existing named-token service saves join the controller's shared DbContext
transaction. Denial, early returns, exceptions and cancellation before commit
dispose the transaction and roll back; no generated secret is retained or revealed.
Non-PostgreSQL unit stores still enforce the role predicate but do not establish
production concurrency evidence.

Admin account edits require an explicitly submitted `Admin`, `Manager` or `User`
role, normalized through Identity and verified to exist before fields change.
Blank, omitted, unknown and unsupported roles leave persisted state unchanged.
Admins can deliberately replace the complete membership of multi-role or roleless
accounts. Protected single-role forms retain DisplayName and Role when submitting
activity/protection changes; ambiguous memberships require a visible selection.
The literal default `admin` account remains immutable in this editor, and usernames
cannot change.

One shared-context relational transaction encloses account fields, checked Identity
role replacement/update, checked security-stamp invalidation and the success audit.
Identity keeps updating the parent `AspNetUsers` row, so promotion conflicts with the
Manager token lock through commit (#823). Exact single-role membership is retained
without rewriting role rows; account/stamp updates still occur. Identity failures,
store/audit exceptions and cancellation before commit roll back the transaction.
The synchronous `LogAudit` save participates in it; external success logs follow
commit. Failed edits do not call saving error helpers. Identity's intermediate
methods do not accept RequestAborted; the enclosing transaction observes it before
mutation and commit. Cancellation after durable commit and ambiguous commit outcomes
cannot establish rollback. No schema or password-credential change is needed (#824).

## Antiforgery

Cookie-authenticated browser mutations require antiforgery. Trip Editor mutations and other browser POST/PUT/PATCH/DELETE actions use the existing MVC antiforgery mechanisms.

Bearer-only APIs do not require MVC antiforgery. For endpoints that support both authorities, preserve cookie priority: `CookieFirstAntiforgeryAttribute` requires a valid antiforgery token whenever an authenticated cookie selected the request, even when a bearer header is present. Only a successfully resolved bearer without selected cookie authority can bypass the cookie token.

## Effective client IP and forwarded headers

Forwarded client identity is trusted only through `TrustedProxyConfiguration`. With no configured trusted proxy/network, forwarding is disabled. Configured trust is explicit and limited to one hop.

Rate/admission code should use the effective post-forwarding client address (for example through the existing rate-limit helper), never parse `X-Forwarded-*` headers independently. This prevents callers from choosing their own admission identity.

## Application-owned admission boundaries

Several finite resources have separate admission owners. Preserve the correct one when adding work:

- Identity attempts bound selected credential/recovery/token traffic.
- API-token lookup admission bounds database credential resolution.
- location-ingestion admission bounds active check-in/location work per user/process while allowing persisted idempotent replays to short-circuit.
- `SseAdmission` bounds active SSE connections by process and effective identity/IP.
- provider/integration services have their own contact, usage and concurrency bounds.

These are not interchangeable rate limiters. Admission should happen before expensive or externally visible work and should release on every terminal path.

## Request and upload sizes

`DynamicRequestSizeMiddleware` applies Wayfarer's fixed application request ceiling before body consumption. Marked user-upload endpoints then pass through `UserFileUploadMiddleware`, which applies the database-backed upload policy after authorization but before antiforgery/form model binding and respects any stricter endpoint/current host limit.

Do not raise Kestrel/form/action limits in isolation. A new upload path needs one coherent ingress ceiling and explicit ownership of staging/cleanup.

## Browser response policy and public embeds

`BrowserResponsePolicyMiddleware` owns the final browser framing/content headers. Ordinary responses use self-only framing plus `X-Frame-Options: SAMEORIGIN`, `X-Content-Type-Options: nosniff` and `Referrer-Policy: strict-origin-when-cross-origin`.

Only controllers that have already proved an effectively public embed may call the explicit embed opt-in. Successful HTML embeds allow external `frame-ancestors`; errors, status re-execution and non-HTML responses do not inherit that relaxation.

Do not add ad-hoc header exceptions in views or downstream middleware.

## Secrets, PII and diagnostics

Never log passwords, bearer/API tokens, Data Protection key identifiers/material, provider credentials or secret-bearing outbound URLs. Location coordinates, origin URLs and request bodies can also be private even when they are not credentials.

Current secret-bearing/provider HTTP clients remove default `HttpClient` logging where query strings or URLs could disclose credentials or private data. Diagnostics should record bounded identities, status/classification and exception type rather than sensitive payloads.

Public DTOs should deliberately project data instead of serializing tracked entities indiscriminately.

## External HTTP and SSRF

Outbound integrations must use the existing owner for that provider/product. Personal-provider contact goes through protected credential resolution and explicit contact/usage admission. Routing uses bounded executors/attempt coordination.

Image-origin fetching is an SSRF-sensitive boundary. `ImageOriginTransport` performs DNS/IP checks against private/loopback destinations and is used by the image proxy and public trip image path to resist DNS rebinding. Do not replace it with an unconstrained `HttpClient` for user-controlled URLs.

External calls need bounded time, response size/contact count as appropriate, cancellation and deterministic handling of redirects/authority. Tests must use fake handlers/clients or local fixtures; do not contact real third-party services.

## Data Protection and provider credentials

Wayfarer configures one durable ASP.NET Core Data Protection application identity, `Wayfarer`, and persists its key ring through the resolved Data Protection authority. The same stable authority protects Identity framework material and personal provider credentials.

`PersonalProviderCredentialService` owns protected provider secrets; `PersonalProviderContactGate` owns current permission/admission to contact a selected provider. Contributors should depend on those services instead of reading ciphertext or legacy plaintext columns directly.

Startup validates that the selected key authority can round-trip and that retained protected provider credentials are readable. Advanced native compatibility/key-ring selection belongs to the [native self-hosting guide](../self-hosting/native-manual.md), not ordinary feature code.

## Security tests

Prefer a small behavioral test at the policy owner: controller/action descriptor tests for antiforgery/authorization metadata, service tests for ownership/admission, relational tests for database-enforced invariants, and bounded HTTP-handler tests for external transport. See [Testing](testing.md).
