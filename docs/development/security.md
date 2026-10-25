---
title: Developer security model
---

# Developer security model

This page describes security boundaries contributors must preserve in application code. Host hardening, TLS, backup, update and operator-secret procedures belong to the [self-hosting documentation](../self-hosting/index.md).

## Identity, roles and password behavior

Wayfarer uses ASP.NET Core Identity with `ApplicationUser` and the `Admin`, `Manager` and `User` role model. Authorization is not only a role check: private resources also require the owning user, group membership/management or another resource-specific relationship.

Production Identity mutations use the shared `IdentityPasswordPolicy`: at least 15 characters with upper/lowercase, digit and non-alphanumeric requirements. Development retains its existing development-seed compatibility. Account lockout is enabled for new users: five failed attempts produce a 15-minute lockout.

Selected Identity credential/recovery/token routes also use application-owned attempt admission. Keep that boundary separate from general `/api` token admission.

Every application-cookie presentation checks the current account's existence,
activity and security stamp through `ActiveAccountCookie` in Identity's existing
cookie validation hook. The database projection bypasses tracked account state;
the normal Identity stamp validator still owns periodic principal renewal. A
stale activity claim or the periodic stamp interval cannot authorize a request.
Rejected sessions are signed out. Navigation explains the ended session on the
login page; API/JSON/fetch challenges return a non-success response with login
guidance. Editor writes retain unsaved drafts and do not report success (#907).

Admin account edits already commit stamp invalidation with their account update.
Manager edits now save account fields through `UpdateSecurityStampAsync`, so the
same Identity save persists activity and invalidation. Deactivate/reactivate
therefore requires fresh website login even if the old cookie was never used
while inactive. Existing API tokens are neither rotated nor deleted: they deny
access while inactive and may work after reactivation under their existing rules.

This is request-time authorization. Work admitted while active may finish after
deactivation. No account lock spans the request; background work and established
streams are outside this admission guarantee. Existing stronger domain checks
remain in place. Public visibility and anonymous sharing do not depend on the
owner's activity and are unchanged.

### Shared path coverage

The source reconciliation for #907 covers these distinct paths rather than adding
an activity check to every action:

| Endpoint family | Current-account authority |
| --- | --- |
| Admin, Manager and User MVC CRUD; Identity Manage Razor Pages | Application-cookie validation, then existing roles/ownership/antiforgery |
| Cookie APIs: editor, Backfill, Groups, Invitations, Users, Visit, cookie Location actions and authenticated SSE subscriptions | The same cookie validation before attributed or manual identity checks |
| Manual owner routes, including private `Trip/Export*` and identity-based API actions without `Authorize` | Rejected cookie removes trusted identity before the existing owner predicate |
| Mixed Activity, Location PUT and Trip search | Cookie priority in `GetUserFromTokenOrCookie`; presented rejected/expired cookies cannot fall back to another bearer owner; active cookie mutations retain antiforgery |
| General bearer Trip/Location CRUD and conditional writers | Existing `IncomingApiTokenResolver` activity check through `BaseApiController` or the direct resolver |
| Mobile Groups, Visits, Routing and SSE; account proof | Existing mobile accessor/resolver; account proof uses the resolver directly and remains token-only |
| Full bearer Trip reads | Resolver bound inside the coherent read context; no global bearer pre-resolution |
| Public Trips/timelines/exports/SSE, tiles, icons, tags, settings and version | Existing anonymous/public projections and visibility rules |

`ActiveAccountCookieTests` exercises representative MVC, Razor, editor writes,
manual/mixed routes, token recovery and public export through production
registrations. Existing cookie-antiforgery and token/coherent-read tests retain
their distinct authority contracts. API error normalization preserves deletion
cookies even when replacing an empty error response or handling an exception.

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

## Trip edit write coverage

The [seven unconditional scalar adapters](api.md#rejected-unconditional-writers-853-reconciliation-f1)
reject owned requests with 409 `mutation-originals-required` and no mutation or
freshness/cache effects. Missing and foreign scope share 404 without exposing
current values. Web authentication, User role and antiforgery remain required;
bearer actions resolve the actual incoming token without cookie fallback.
Supported scalar edits use the existing mutation owners with genuine client
originals, selected-field writes, locked current authority and current-safe Trip
freshness. Segment combined route/Notes saves remain atomic. Standalone Area
geometry retains its documented exclusion. This F1 correction does not settle F2
metadata uncertainty or constitute C1/C2/C5 acceptance.

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

## Build and runtime certificate trust (#915)

The [published assessment](https://github.com/stef-k/Wayfarer/issues/915) binds the
affected artifacts at source `ccd107fa9720492a7d7ef067615d3e6babe032d6`.
Node and pip carry roots separately from the OS. A patched host alone does not
correct an old Node or pip bundle. Keep ordinary TLS verification enabled.

Both CI venvs call `tools/ci/bootstrap-pip.sh` before any dependency install. Its
direct pip 26.2.2 wheel/hash was rechecked against the
[Python security notice](https://discuss.python.org/t/information-for-python-users-regarding-emudhra-emsign-ca-certificate-security-incident/109576)
and PyPI wheel metadata. Isolated pip disables indexes, dependency resolution,
cache reuse and version checks during bootstrap. Installed vendored roots must
exclude emSign ECC Root CA G3 and retain ISRG Root X1 as a positive control.

The Dockerfile pins Node 24.22.1 at index
`sha256:55fb5f5a527ea2c5c4a95f963e48ef90aa38c7b346054fdc8ee9957a2b55d205`.
Signed apt package verification installs Bookworm CA `20250419~deb12u2` before
Cargo and Noble CA `20260816ubuntu0.24.04.1` before NuGet and in the final runtime.
The pinned Rust compiler and .NET bases stay unchanged; CA package acquisition
uses their existing signed distribution authority. Rebuilding old bases without
an explicit CA correction does not repair their stores.

Browser provisioning transiently mounts patched Node but executes the exact
published Playwright CLI. Package 1.63.0 and Chromium revision 1243 stay aligned.
The old private runtime driver is not a supported external HTTPS client: capture
continues to permit only server-owned HTTP loopback fetches. Embedded Chromium
distrust/CRLSet contents remain unqualified; do not infer protection from its
version or assume background updates.

New bundles select Caddy 2.11.7 at index
`sha256:eb4d35b7452d1ab6171fbdbe8d9275b2301dad6dad92115d91e19fb9b24d510f`.
Its Alpine CA/bundle `20261020-r0` removes G3. The inspected AMD64 executable uses
Go 1.26.8, before Go's additional intermediate denylist fix; qualification relies
on the nonempty corrected system store. No empty-store/fallback-root claim is
made. Local ingress tests do not qualify public ACME or a production host.

Historical Caddy digest `sha256:6aeddd44c3078b0f9a35206472a11420648a79c184603ef95957d0a20044cb2b`
remains readable for retained release/recovery authority. Runtime checks bind
Caddy to the selected bundle fingerprint, including historical bundles. New
assembly selects only the corrected digest. The existing unchanged-proxy update
gate still rejects crossing these digests; this change does not authorize a
proxy transition or bypass release, recovery or rollback compatibility checks.

A future separately authorized application-only release must publish new
application images and bundles binding the corrected Caddy artifact. Existing
stable artifacts must never be overwritten. The proxy compatibility gate needs
an explicitly reviewed release path before an existing installation can cross
that boundary. No new version is selected here. CI/build corrections alone do
not need distributable replacement. The assessment demonstrated no independent
replacement need for the database, image helper/codec, capture browser or
standalone recovery/operator binaries solely because of this CA incident.
New bundles will naturally include the operator's updated binding checks.
Native hosts still require their own CA remediation and provisioning checks.

The native/build Node guard inspects its selected default certificates, including
`NODE_EXTRA_CA_CERTS`, together with the required host store. Node's default view
omits lazily loaded OpenSSL roots, so neither view alone proves effective trust.
Valid extra certificates remain supported; G3 additions and missing ISRG X1
positive controls fail closed. See [Node's CA source API](https://nodejs.org/api/tls.html#tlsgetcacertificatestype).

Artifact inspection is distinct from native execution on each architecture and
from public distribution acceptance. In the ordinary test, AMD64 image and native
ARM64 image jobs, APT updates signed metadata and installs `ca-certificates`
through existing distribution keys before the host guard runs. This authenticates
the CA correction independently of the stale TLS root store; no verification is
disabled. Setup actions then use `NODE_OPTIONS=--use-openssl-ca` and explicit
`SSL_CERT_FILE`/`SSL_CERT_DIR` paths to select the checked host roots for their
subsequent tool downloads. The Ubuntu correction is documented in
[USN-8944-1](https://ubuntu.com/security/notices/USN-8944-1).

The first checkout, action archive acquisition, runner-provided executables
(including rustup's own verifier/bootstrap) and service-image pulls precede or sit
outside this guarded download boundary. Classification/cleanup jobs are not host
acquisition qualification either. A later correction cannot protect those earlier
downloads or qualify arbitrary private bundles; actual runner/action/runtime trust
evidence remains required. Source ordering and local AMD64 package controls do not
prove execution on the hosted runners or ARM64. #474/#877 and the broader dependency
audit keep their separate gates.

## Data Protection and provider credentials

Wayfarer configures one durable ASP.NET Core Data Protection application identity, `Wayfarer`, and persists its key ring through the resolved Data Protection authority. The same stable authority protects Identity framework material and personal provider credentials.

`PersonalProviderCredentialService` owns protected provider secrets; `PersonalProviderContactGate` owns current permission/admission to contact a selected provider. Contributors should depend on those services instead of reading ciphertext or legacy plaintext columns directly.

Startup validates that the selected key authority can round-trip and that retained protected provider credentials are readable. Advanced native compatibility/key-ring selection belongs to the [native self-hosting guide](../self-hosting/native-manual.md), not ordinary feature code.

## Security tests

Prefer a small behavioral test at the policy owner: controller/action descriptor tests for antiforgery/authorization metadata, service tests for ownership/admission, relational tests for database-enforced invariants, and bounded HTTP-handler tests for external transport. See [Testing](testing.md).
