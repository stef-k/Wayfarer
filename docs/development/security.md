---
title: Developer security model
---

# Developer security model

This page describes security boundaries contributors must preserve in application code. Host hardening, TLS, backup, update and operator-secret procedures belong to the [self-hosting documentation](../self-hosting/index.md).

## Identity, roles and password behavior

Wayfarer uses ASP.NET Core Identity with `ApplicationUser` and the `Admin`, `Manager` and `User` role model. Authorization is not only a role check: private resources also require the owning user, group membership/management or another resource-specific relationship.

Production Identity mutations use the shared `IdentityPasswordPolicy`: at least 15 characters with upper/lowercase, digit and non-alphanumeric requirements. Development retains its existing development-seed compatibility. Account lockout is enabled for new users: five failed attempts produce a 15-minute lockout.

Selected Identity credential/recovery/token routes also use application-owned attempt admission. Keep that boundary separate from general `/api` token admission.

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
