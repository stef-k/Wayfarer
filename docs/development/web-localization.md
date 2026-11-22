---
title: Web localization
---

# Web localization

The browser preference applies to the web interface. The server resolves a
valid `.AspNetCore.Culture` cookie first, then supported `Accept-Language` ranges
by quality and header order, then `en-US`. A malformed weight excludes its
range and negotiation continues with the other ranges. The current document keeps its
language for its lifetime. A server fragment resolves language from its own
request. Use the document language for browser-rendered feedback.

## Register a locale

Add the canonical tag, native selector label, primary-language default and
cardinal categories to `Resources/WebLocales.json`. Add a culture-specific
`.resx` for each delivered catalog beside its English neutral file. List the
locale's integer cardinal rules in `IntegerPluralRules` using anchored
expressions over nonnegative invariant decimal digits. Categories with explicit
rules must be disjoint. `other` is the default. Browser cardinal selectors use
`Intl.PluralRules` with the same registered tag. The test Polish catalog and
registration demonstrate `few` and `many`; they belong to the test assembly.

## Add a message

- Use feature-qualified, stable keys under `Resources/`. `Shell` owns shared
  chrome, including the language selector, shared map controls, notes controls
  and notifications. `Viewer` owns live itinerary, visit and export text.
  `Editor` owns the Trip editing interface. `PublicPages` owns Home, the Privacy
  interface and Registration Closed. `Discovery` owns public Trip listing and
  preview. `PublicTimeline` owns the full and embedded Timeline interface,
  location details and document errors. `Groups` owns account-area Group and
  invitation workflows, including sharing controls and map-specific presentation.
  `Timeline` owns private live and chronological history. Shell owns the
  shared location Activity controls and protected mutation feedback.
- Name full phrases for their intent and keep argument positions consistent
  between locales. Use `{0}`, `{1}` and subsequent positions for plain-text
  arguments. Put links, HTML, icons and accessibility relationships in the
  consuming Razor, JavaScript or Vue component.
- Use `WebMessages` in Razor and use the shared browser message functions in
  plain JavaScript and Vue. Each layout emits a single inert JSON bootstrap
  carrying the active locale and the feature catalogs needed by that document.
  Use the default JSON encoder. Bind messages as text in Razor and Vue and use
  `textContent` in script. When a legacy widget requires HTML, encode dynamic
  text and attributes before inserting them.
- Use `Count` in Razor and `plural` in JavaScript for cardinal phrases.
  Supply the registered categories such as `.one` and `.other`. Missing or
  invalid translations fall back to readable English and use English plural
  selection. The server reports a bounded set of missing or invalid entries.
- Use `DisplayCulture` or the browser `date` and `number` helpers for display.
  Keep existing timezone semantics. Use invariant formatting for coordinates,
  URLs, request or data attribute values, IDs, serialization and exports.
  Preserve authored text, editable labels, licence terms and provider data.
- Map an async workflow's state or structured message identity and safe scalar
  arguments to the initiating document's catalog. Keep decisions, retries,
  concurrency checks and authorization bound to the existing machine values.

## Support switching on a workspace

For pages that render directly from POST, provide an application-owned safe
GET through `ViewData["LanguageSafeGet"]`. On safe GET pages the selector retains
the whole URL, including query and fragment. Withhold switching on one-time
results and unintegrated custom editing workspaces.

A workspace registers read-only draft checks on its actual owners through
`registerLanguageDraft`. The selector asks before writing the preference and
holds the workspace through that request. Owners must retain drafts after
dismissal, cancellation and preference-write failure. Native unload guards
can consult `isLanguageUnload` to recognize the single navigation authorized by
a successful preference change. Never use a save or cleanup action as the test
for unsaved work.

## Coverage register

| Area | Entry points and owners |
| --- | --- |
| 1 | Standard shell, collapsed navigation, guest/User/Manager/Admin menus, shared error and notification chrome. The authenticated Trip Editor Vue mount and the authenticated, public and embedded live Trip Viewer use the shared locale, their feature catalogs and the shared map/notes chrome. Viewer owns browser print and on-screen export feedback. Capture contexts explicitly request English. |
| 2 | Home, Privacy and Registration Closed. Public Trip grid/list, tag redirect, filters and pagination, quick preview and asynchronous feedback. Full and embedded public Timeline, map and location detail, summary, clipboard and unavailable or failed states. |
| 3 | Identity and personal account. The compiled route inventory, presentation owners and language-navigation evidence are recorded below. |
| 4 | User and Timeline Settings, Hidden Areas, app connection and personal location providers. The affected routes and evidence are recorded below. |
| 5 | User and Manager Groups, invitation inbox and roster operations, sharing, and scoped map history. Route and live feedback owners are recorded below. |
| 6 | Private live Timeline and Chronological calendar. Date and filter controls, map areas, connection rules, analysis, statistics, details and edit return use the document locale. Both Timeline documents use the shared language selector with their draft owners. |
| 7 | Location and Visit CRUD. Ordinary form guarding is shared. Visit Edit registers its mounted Notes as a draft owner. Protected Location workspaces use their existing draft and recovery rules. |
| 8 | Location import, managed-file replay, address enrichment and Location exports. The Transfers catalog covers the Web controls and feedback. |
| 9 | My Trips, import, Compose and Copy. The Vue entry receives the document locale. Switching remains withheld pending integration with the import and composition owners. |
| 10–11 | Manager/Admin accounts, tokens and catalogs, followed by Admin operations. Shared navigation participates. |

Area 1 checks cover these Editor boundaries.

| Source and owner | Evidence |
| --- | --- |
| Shared locale, safe language navigation and Editor draft owners | `WebLanguageHttpTests`, `WebCatalogTests`, `webLanguage.test.mjs`, `tripEditorExit.test.mjs` and `tripEditorLanguageDrafts.test.mjs` cover negotiations, preference writes, retained work and safe reload. |
| Camera toolbar and viewport | `editorNavigationLanguage.spec.ts` uses animated Leaflet to check Focus, immediate Recenter and the saved camera through reflow. It queues Fit All during a keyboard pan and resizes the map during the ensuing toolbar pan. Greek success follows the terminal center and zoom. Navigation keeps Metadata edits. `tripMapViewport.test.mjs` covers gesture-only capture and camera URL context. |
| Close, Cancel and shared confirmations | The same managed journey checks dirty docked Close. `consumerNotes.test.mjs` checks expanded Close and Area and Place Cancel, Escape focus restoration, and discard with an independent Planning draft retained. Region, Place and Area actions use the Editor catalog for dirty prompts and destructive or dependency review. |
| Entity changes, sharing and recovery | The mounted Editor coverage in `consumerNotes.test.mjs` includes Region save status, first-publication confirmation, unconfirmed Trip settings and sharing, map search feedback, credit validation, waypoint validation and draft-preserving Segment proposal and stale-route recovery. Shipped catalogs own live status, help, confirmation and recovery text. Authored names, transport labels and request values flow through unchanged. |
| Place coordinate work and draft names | Mounted English and Greek Place forms check that renaming during a Leaflet pick retains the marker and selected coordinate. Done adopts the pick and Cancel restores the draft coordinate. Greek creation and Reset localize the suggested Region and Place names and retain names from search and saved records. |
| Planning and Check, Visit Progress and Notes | Mounted checks cover an unconfirmed Planning save and its draft and save hold, scoped findings and estimate evidence, retained check inputs, canonical vs. authored Region labels, and Notes draft protection. |

The Shell catalog supplies Leaflet.Draw's polygon prompts and crossing warning.
The Editor catalog supplies its route prompts. The Shell catalog also supplies the Editor and Viewer ruler
instructions and the metric labels for rulers and live route drawing. The managed
Greek map-controls journey exercises both rulers through Escape and follows the
drawing prompts through polygon validation, live route measurements and custom
route completion.

The headed Viewer-to-Editor journey checks the camera and active Segment
through the preference switch, Greek Editor validation, and contrast in dark
desktop, narrow and native 200% zoom layouts. The delayed Place focus journey
checks that Place details open with a linked camera and Segment retained, and
that a Place-only link and deliberate Place selection still move the map.
The PostgreSQL private Viewer
test checks a coherent split-query snapshot with ordered waypoints and
owner-filtered visits. See [#960](https://github.com/stef-k/Wayfarer/issues/960)
for the remaining areas and review gates.

Area 2 checks exercise the public routes and browser presentation.

| Source and owner | Evidence |
| --- | --- |
| Home, Privacy and Registration Closed | `PublicWebLanguageHttpTests` checks Greek page copy and alternate text. The Privacy title and links follow the locale and the policy prose remains inside `lang="en"`. |
| Discovery | The same tests run the Greek grid and list against PostgreSQL with search, tags, sort and pagination. They check query retention and the tag redirect. The quick-preview Razor fragment negotiates its own language and responds with private, revalidating cache headers and language/cookie variation. |
| Public Timeline and both iframe documents | The routed tests check preference-cookie precedence, cookie-free negotiation and English fallback, the two framing policies, equivalent missing/private Timeline documents, and authored headings. `PublicTimelineLocationPostgresTests` exercises public data and statistics eligibility and the legacy failure response. `embeddedMap.test.mjs` checks cooperative behavior shared with the shipped Trip embed. |
| Timeline browser feedback | `publicTimelineLanguage.test.mjs` exercises location-detail text, localized values and the existing viewer and recorded timezone basis. It checks empty, unavailable and failed responses using the public API envelope. `publicLanguage.spec.ts` is the managed Greek Timeline embed smoke for the empty map and clipboard announcement. |

Use the current [#960 contract](https://github.com/stef-k/Wayfarer/issues/960)
for the route, role and message-owner inventory. Keep untranslated destination
content marked as English where practical. Record evidence when a delivery is
accepted.

## Validate

Run the regular `WebCatalogTests` and `WebLanguageHttpTests` .NET selections and
`npm run test:client`. Run typecheck, build and Trip Editor asset checks for
frontend changes. Use the managed shared-layout browser procedure in
[Testing](testing.md#browser-and-playwright-evidence) for the integrated
Viewer-to-Editor journey and native browser zoom evidence.

### Area 3 Identity and personal account

The compiled inventory has 31 Identity page models. The account routes below
use the `/Identity/Account` prefix. `Manage/Index` also serves the Manage folder
URL. Razor overrides use the page models from Identity UI 10.0.14. The package
continues to provide handlers except for Wayfarer's existing Login, Register,
Manage Index and EnableAuthenticator models. On a package upgrade, check the
compiled route inventory, templates, validators and handler message map against
the new package. [ASP.NET Core Identity UI source](https://github.com/dotnet/aspnetcore/tree/v10.0.14/src/Identity/UI/src/Areas/Identity/Pages/V5)

| Compiled routes | Presentation owner |
| --- | --- |
| `Login`, `Register`, `Logout`, `AccessDenied`, `Lockout`, `/Identity/Error` | Identity catalog and shared shell. Login includes inactivity, session expiry and configured provider callback errors. |
| `LoginWith2fa`, `LoginWithRecoveryCode`, `ExternalLogin` | Package-backed overrides retain pending sign-in cookies, callbacks, provider values and return URLs. |
| `RegisterConfirmation`, `ConfirmEmail`, `ConfirmEmailChange`, `ResendEmailConfirmation` | Identity catalog at the rendered destination, including confirmation status and resend outcomes. Outbound mail follows the existing handlers. |
| `ForgotPassword`, `ForgotPasswordConfirmation`, `ResetPassword`, `ResetPasswordConfirmation` | Package-backed overrides, field and Identity policy errors, and an Identity message for missing reset codes. |
| `Manage/Index`, `Manage/Email`, `Manage/ChangePassword`, `Manage/SetPassword`, `Manage/ExternalLogins` | Localized nested layout, navigation and status partial. Profile bootstraps the ProfilePhoto catalog for its independent crop editor and API feedback. |
| `Manage/TwoFactorAuthentication`, `Manage/EnableAuthenticator`, `Manage/Disable2fa`, `Manage/ResetAuthenticator`, `Manage/GenerateRecoveryCodes`, `Manage/ShowRecoveryCodes` | Identity catalog, recovery-code counts and instructions. Authenticator keys, QR values and recovery codes pass through unchanged. |
| `Manage/PersonalData`, `Manage/DeletePersonalData`, `Manage/DownloadPersonalData` | Localized data controls and refusal feedback. The download handler returns 404 on GET and the existing JSON attachment on POST. |

`IdentityAttemptRouteTests` pins model ownership, Manage authorization and
antiforgery. It checks Greek and English attempt-limit bodies and the original
status and retry headers. `IdentityAdmittedFlowTests` covers lockout, pending
sign-in and single-use recovery, the generated-code result, reset and confirmation
tokens, and the configured external-provider state with a fixture scheme.
Provider callbacks, token-processing pages and one-time results withhold
language switching.

`WebLanguageHttpTests` exercises local Login and packaged Manage Email rendering,
including the nested layout, a named-handler failure, destination-localized
TempData and byte-identical personal data download. On an admitted ordinary form
failure, the selector requests consent and uses the application's safe GET.
The routed layout fixtures supply the same settings dependency production startup
supplies, independently of the database's initial settings rows.

`WebCatalogTests` checks both catalog pairs. The managed Chromium profile-photo
smoke checks a fresh GET at the same URL after Login validation, Greek photo
feedback, retaining a selected crop after cancellation and a failed language
write, and one safe GET after a successful switch. A photo
write or an unconfirmed outcome temporarily disables switching until the owner
has refreshed the photo state.


### Area 4 settings, connection and privacy

| Entry points | Presentation and evidence |
| --- | --- |
| `/User/Settings` and `/User/Timeline/Settings` | Settings owns logging thresholds, public availability, live acknowledgement, custom delay guidance, field errors and bulk deletion. Shell owns embed controls and clipboard feedback. Routed Greek validation in `WebLanguageHttpTests` checks the live warning, the persisted privacy state and safe GET; the controller tests cover opt-in and unchanged delay rules. The Time Threshold label association is tracked in [#993](https://github.com/stef-k/Wayfarer/issues/993). |
| `/User/HiddenAreas` list, create, edit and delete | HiddenAreas owns pages, validation and drawing controls. Shell supplies shared polygon prompts and measurement chrome. `SettingsWebLanguageHttpTests` exercises the Greek Edit form and retains exact coordinates and SRID. The polygon editor registers its map state and WKT field with the selector. `webLanguage.test.mjs` checks unfinished and committed polygon-only drafts, cancellation, failed preference writes and safe navigation. |
| `/User/ApiToken` | Connection owns issue, replace, reveal, QR, copy, hide and recovery text. `SettingsWebLanguageHttpTests` checks the Greek route and its selector exclusion. `connectApps.test.mjs` covers the one-time reveal, cleanup and safe status refresh. Tokens, QR contents and mutation responses remain outside locale data. |
| `/User/LocationProviderSettings` | Providers owns migration, verification, capability selection, credential and consent controls, usage guards, revocation and backfill. `ProviderSettingsMigrationRouteTests` follows an English POST to its Greek GET and verifies the credential remains masked. `ProviderStatus` carries a catalog key plus a fixed capability value where needed. The destination formats the message. |


The managed Chromium check reached the Greek drawing toolbar at a narrow width
in dark mode. Restoring the Leaflet stylesheet flag made the polygon control
clickable. The browser fixture ended with an empty WKT field after its rapid
vertex clicks. The complete drawn-polygon browser journey remains unqualified.


### Area 5 Groups and invitations

| Entry points | Presentation and evidence |
| --- | --- |
| User Groups list, create and leave. Manager Groups list, create, edit and delete. | Group type labels and field validation use the Groups catalog. The active membership role determines which management actions render. MVC change alerts carry a catalog key for the redirect destination. |
| User and Manager Members. User Invitations inbox. | The shared Members client updates roster and pending invitations via the existing endpoints and SSE hints. The invitation-row helper reconciles refreshed Razor rows against confirmed removals, formats their dates in the document locale and preserves their actions. The inbox localizes accept and decline feedback and keeps authored Group and account fields as text. Shell keeps ownership of site-wide notices and badges. |
| User and Manager Group maps. | Sharing controls use the current policy and role. The drawer names and exposes its controls when open and restores focus on close. The map localizes selected-period feedback, marker status and details. Scope, calendar inputs, coordinates sent over the wire and current-membership access follow the existing server contract. |
| Focused route and client checks | `MobileGroupWorkflowPostgresTests` verifies Greek Owner, Group Manager and Member actions, a pending row and an English POST whose duplicate refusal renders in Greek at its redirect destination. `groupInvitationRows.test.mjs` verifies the same Greek refusal at the AJAX boundary and a refreshed pending row with its viewer-zone timestamp and cancel action. Group route, sharing and history tests continue to exercise the existing permissions and data contracts. |
| Mounted check | `unavailableGroupInvite.spec.ts` checks the real stale-account rejection in Greek and the narrow map with localized drawer actions, focus restoration and a scoped history request. |

### Area 6 private Timeline

| Entry points | Presentation and evidence |
| --- | --- |
| `/User/Timeline` | Timeline owns the generated heading, viewport and live feedback, summary, geographic details and record actions. Authored headings and location data render as text. Map bounds, SSE and all-history statistics keep their existing requests. |
| `/User/Timeline/Chronological` | The calendar uses localized civil dates, scope and weekday names. Browser-derived week start and the frozen value on a Week link retain their authority. Filters, saved rectangles, paging, time basis and analysis use the same request values. The calendar renders partial, empty, busy, failed and session-ended results with separate operation feedback. A matching-rules change rejects mixed observations and explains how Refresh revalidates the retained filters. Dot decimals and scientific notation remain the connection-rule input syntax. |
| Calendar and map location detail and edit return | Detail keeps recorded and viewer time, the Location mutation owner and its inspected conditional deletion. The calendar also displays Event UTC with every supplied fractional digit. Timeline links carry the current map or calendar URL. Private filter values remain in tab storage. The independent Location Edit workspace uses the active locale and withholds its selector. Its form belongs to Area 7. |
| Language draft and verification | The selector queries the Calendar editor and connection rule draft, inline Activity work and active protected deletion before changing language. `privateTimelineLanguage.spec.ts` exercises a frozen Week in English and Greek, canceling a switch with a retained dot-decimal rule, then checks the Greek date and failed read at the same scope. It also checks the Greek rules dialog at narrow width in dark mode and the live Timeline heading and statistics. `timelineCalendar.spec.ts` exercises range, filters and protected edit return. Existing catalog, calendar, geographic endpoint and Location mutation tests cover the underlying contracts. |

Area 6 was accepted and merged in [PR #1002](https://github.com/stef-k/Wayfarer/pull/1002).
Independent re-review at `37e2840f9f5c11045245a138ae7ba4dbcceccee4` verified
matching-rule rejection and refresh with the same scope and filters, and distinct
Event UTC values at full fractional precision. The correction selection passed
42 client and 59 .NET tests, with one opt-in PostgreSQL discovery case skipped.
The exact-head Chromium replay covered the Greek frozen Week, the narrow dark
connection-rule dialog and the Greek live Timeline. The unchanged paths carried
forward protected-edit return and the broader focused test evidence recorded in
the PR. Inherited Timeline defects remain tracked in
[#994](https://github.com/stef-k/Wayfarer/issues/994),
[#1001](https://github.com/stef-k/Wayfarer/issues/1001) and
[#1003](https://github.com/stef-k/Wayfarer/issues/1003).

### Area 7 Locations and Visits

| Entry points | Presentation and evidence |
| --- | --- |
| `/User/Location` and `/User/Location/AllLocations` | The Locations catalog covers search, map and table, statistics, detail, address and Activity controls. The existing protected deletion owner supplies the inspected terms and receipt for localized feedback. Bulk deletion retains its current-content semantics and instructs the user to inspect history after an unconfirmed reply. |
| `/User/Location/Create` and `/User/Location/Edit/{id}` | Localized form and field feedback uses invariant coordinate, measurement and wall-time inputs. Create retains its original attempt identity and current authored controls. Protected Edit keeps the retained draft, original comparison evidence, time-zone recovery and safe return URL. Shell owns shared map and rich Notes controls, including link and image actions. |
| `/User/Location/BulkEditNotes` | The form localizes filters, append, replace, clear, advisory preview and the committed count. Its ownership and matching at Apply time follow the existing bulk operation. |
| `/User/Visit` and `/User/Visit/Edit/{id}` | Visits owns history, generated counts and status, filters, linked locations, UTC arrival and end fields, marker choices, and single and bulk deletion feedback. Stored icon and color keys and authored names, addresses and Notes retain their existing values. MVC redirect alerts resolve their message in the destination locale. |
| Language switching | The Location lists consult the inline Activity draft owner and the split map consults its ruler. The lists hold switching during protected deletion preparation and execution. Conditional recovery uses the retained attempt and explicit inspection. An unconfirmed bulk deletion holds switching until the user deliberately refreshes history. Create, Edit and Bulk Notes withhold switching because they can retain mounted input or recovery state. Visit Edit uses the ordinary form and mounted Notes guards and an owned GET after validation. |
| Focused evidence | `protectedLocation.spec.ts` exercises Greek Visit History, Greek Create binding and field feedback, protected Edit recovery and a real stale save. The assertions use the existing routes and check numeric, time, draft and return inputs. The existing English Create retry journey still checks preservation of unacknowledged edits. Area 7 independent review is pending. |


### Area 8 Location transfers and enrichment

| Entry points | Presentation and evidence |
| --- | --- |
| `/User/LocationImport` and `/api/sse/import` | The import list localizes persisted lifecycle, custody, checkpoints, counts, warnings and control outcomes. The existing enrichment projector determines available actions and holds. SSE carries a content-free refresh hint, so initial and refreshed documents use the same presentation. `locationTransferLanguage.spec.ts` checks a Greek stopped import before and after the reload. |
| `/User/LocationImport/Upload` | Supported choices preserve their submitted format values. The Greek browser journey checks an invalid extension and the retained format choice. `UploadRequestBoundaryTests` exercises Kestrel and the real upload middleware for disabled and oversized requests, including a streamed oversized request. Authorization still precedes policy. |
| Location export menu and `/LocationExport/{action}` | All four download controls and the error document use the Transfers catalog. `LocationTransferWebLanguageHttpTests` compares English and Greek GeoJSON bytes and checks the media type, filename convention, authored content and recovery metadata. It also checks the Greek error document for a GPX serialization failure. The existing PostgreSQL round trips exercise all four formats. |
