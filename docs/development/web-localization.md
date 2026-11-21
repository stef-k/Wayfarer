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
  `Editor` owns the Trip editing interface.
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
| 2 | Public landing, discovery, public Timeline, privacy interface, registration-closed screen and public location. The public Timeline embed layout uses the shared preference. |
| 3 | Identity and account pages, including packaged pages. Shared layout participates and one-time routes withhold the selector. |
| 4–8 | Private Timeline, group and invitation workflows, account/app connection and settings, location/visit editing, and location transfer and enrichment. Ordinary form guarding is shared. Visit Edit registers its mounted Notes as a draft owner. Timeline Settings supplies its safe GET. Unintegrated protected workspace, Location, and connection routes withhold switching. |
| 9 | My Trips, import, Compose and Copy. The Vue entry receives the document locale. Switching remains withheld pending integration with the import and composition owners. |
| 10–11 | Manager/Admin accounts, tokens and catalogs, followed by Admin operations. Shared navigation participates. |

Area 1 verification covers language negotiation, preference writes, core draft
guards, the live map lifecycle and Editor/Viewer presentation. The mounted
Editor tests cover Greek indexed waypoint validation, an expired route
proposal, and the Segment route summary before and after stale reload. They
also cover map search and discard feedback, authored and canonical Region
labels in the Area and Place editors, map toolbar, Place popup and Visit Progress,
marker titles and text alternatives, Region collapse
and deletion controls, credit validation and retry, and an unconfirmed Planning
save with its draft and save hold intact. Plan Check presents scoped findings,
dependency changes and structured estimate evidence. Follow-up controls use
the original finding values and retained check inputs.

The Editor catalog supplies Leaflet.Draw's polygon and route prompts and the
polygon crossing warning. The Shell catalog supplies the Editor and Viewer ruler
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
