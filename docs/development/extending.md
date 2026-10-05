---
title: Extending Wayfarer
---

# Extending Wayfarer

Use the existing concrete owner for a change. These recipes show the normal path and the smallest useful proof; they are not extension frameworks.

## Add or modify an MVC/Razor feature

**Start with:** the matching controller under `Areas/Admin`, `Areas/Manager`, `Areas/User` or `Areas/Public`, its `Views/`/area view, and the owning service under `Services/`.

Keep HTTP binding, coarse authorization and antiforgery in the controller. Put reusable domain/workflow behavior in the existing service. If JavaScript is view-specific, follow `frontend.config.yaml` and place it under the matching `wwwroot/js/Areas/{Area}/{Controller}/{Action}.js` or `wwwroot/js/{Controller}/{Action}.js` path.

Check role/ownership, cancellation, request-size rules for uploads, `StoragePaths` for files and privacy of diagnostics.

**Lowest useful test:** controller/service behavioral test proving the requirement. Add a browser smoke only when the behavior cannot be credibly proved below the browser.

## Add an API endpoint

**Start with:** the cohesive controller in `Areas/Api/Controllers/`; use its existing service and DTO conventions. For Trip Editor work, extend the `TripEditorController` partial family rather than introducing a parallel API.

Choose the authority explicitly: public, cookie Identity, API token/mobile bearer, or an existing dual-auth pattern. Enforce ownership after authentication. Cookie browser mutations require antiforgery; bearer-only paths do not. Preserve cancellation and the existing `/api` error/request-ID boundary.

If the endpoint creates retryable location work, preserve the established admission/idempotency owner instead of adding a client-only dedupe check.

**Lowest useful test:** controller/action-boundary test for auth/antiforgery plus a service test for behavior. Use PostgreSQL only when the contract depends on constraints, transactions, spatial queries or concurrency.

## Add a persisted field or entity

**Start with:** `Models/`, the current `ApplicationDbContext`/`Models/Configuration/` owner and `Migrations/`.

Define nullability/defaults, relationships/delete behavior and required indexes/constraints. Spatial fields use NetTopologySuite/PostGIS with SRID 4326 and the appropriate geography type/index. If concurrent writers can violate an invariant, enforce it in PostgreSQL or use the existing concurrency mechanism.

Generate a focused migration with the repository-local `dotnet-ef`, inspect it and update only the DTO/service/UI boundaries that consume the new data.

**Lowest useful test:** model/service unit test when provider behavior is irrelevant; guarded PostgreSQL test when migration, PostGIS, uniqueness, transaction or concurrency semantics matter.

## Add or change a Quartz background job/workflow

**Start with:** `Jobs/` for the `IJob` adapter and the existing workflow/service namespace for durable state transitions. Startup registration/scheduling is in `Program.cs`; import/enrichment workflows additionally use `Services/LocationImports/` or `Services/LocationEnrichment/`.

Keep the job thin. Durable workflow state, epochs/idempotency, transaction ownership, cancellation and recovery/reconciliation belong to the service that can be exercised independently of Quartz. Use scoped dependencies through the existing job factory.

**Lowest useful test:** execute the workflow/service directly. Add Quartz/PostgreSQL evidence when persisted scheduler identity, reconciliation or race behavior is the requirement.

## Add a location importer or exporter

**Importer start:** `Parsers/ILocationDataParser`, the concrete parser files and `LocationDataParserFactory`; import execution is owned by `LocationImportService` with staging/lifecycle under `Services/LocationImports/`.

Parsers stream `Location` records in source order and accept cancellation. Add the new file type to the existing enum/detection/factory path rather than bypassing import lifecycle. Preserve bounded staging, deduplication, user ownership, timestamp/spatial conventions and private diagnostics.

**Exporter start:** the existing user location export controller/services or Trip import/export services, depending on the domain. Preserve publication/privacy projection and streaming/bounded-memory behavior.

**Lowest useful test:** parser/export format tests. Add relational import tests only for persistence/deduplication/transaction behavior.

## Add or change an external provider/integration

**Start with:** `Services/LocationProviders/`, `Services/ExternalRouting/`, `Services/LocationEnrichment/` or the existing service that owns that external product.

Use `PersonalProviderCredentialService` for protected personal credentials and `PersonalProviderContactGate`/the existing provider admission owner before contact. Preserve bounded attempts/concurrency/time/response size, cancellation, credential and coordinate privacy, and diagnostic suppression for secret-bearing HTTP. User-controlled URLs must keep the repository's SSRF-safe transport.

Do not make real third-party network calls in tests. Use a fake `HttpMessageHandler`, provider client or gate at the existing seam.

**Lowest useful test:** deterministic service/transport test that proves request construction, admission and response classification without external network.

## Add Razor/area-aligned JavaScript behavior

**Start with:** `frontend.config.yaml` and the matching source under `wwwroot/js/`. Global behavior belongs in `wwwroot/js/site.js` only when it is truly global.

Keep server-owned authorization/data rules on the server. JavaScript may orchestrate UI state and call APIs but must not become the only owner of an invariant. Preserve Bootstrap conventions and the existing generated `wwwroot/dist` boundary.

**Lowest useful test:** `tests/client/*.test.mjs` for deterministic client behavior; add one browser smoke for integration with rendered markup only when necessary. Run `npm run test:client` and `dotnet frontend build`/relevant build checks.

## Add or change Vue Trip Editor behavior

**Start with:** `ClientApps/trip-editor/src/App.vue`, its components/state/API modules, `Areas/Api/Controllers/TripEditor*.cs` and the Trip Editor mutation services registered in `Program.cs`.

Vue owns interactive editor state; ASP.NET owns authorization, persistence and durable mutation rules. Keep request cancellation, antiforgery on cookie mutations, ownership and aggregate/concurrency tokens intact. Vite output belongs under `wwwroot/vite/trip-editor/` and must not be hand-edited.

**Lowest useful test:** Vue/client test for client state or API module behavior and a focused controller/service test for the server mutation. Use browser evidence only for a real cross-boundary user journey.

## Documentation and validation

When a change alters a developer-visible contract, update the owning page in `docs/development/` rather than appending implementation history to a legacy numbered guide. Run the relevant commands in [Testing](testing.md), including Code Guard before handing the branch to review.
