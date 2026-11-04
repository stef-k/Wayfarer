# Independent correction review: #961 / draft PR #968

**Verdict: technical PASS with bounded validation debt. No blocking findings. F1 is corrected.**

Reviewed head: `c79ebfde810e0d8da32019c63aa174da9824338a`.
Base: `31303f7b2ab4ac6ca347f0c98c1d2356841fefdc`.
Previous reviewed head: `b9376a27a6612cf7af9495cfc2c23c54c29a569a`.
Review date: 2026-11-04.

This session reviewed the complete 21-file branch and nine-file correction delta
directly, in a fresh isolated detached checkout at `source/`. Live GitHub revisions
matched before review and at the closing check. The live issue, checkout AGENTS.md,
complete previous review, implementation report and navigation inventory were read.
No delegation, implementation, commits, pushes, tracker writes, merge, closure,
release or deployment occurred. The checkout remains clean. The shared checkout
and original evidence packets are preserved.

## F1 resolution and navigation coverage

The retained original probe and correction-baseline reproduction both show the
same real failure: a 1280px workspace, 1260px Custom sidebar and 1380px horizontal
padding drive the original route from zoom 12 to 19; the entire map then fails to
contain it. Those historical product FAILs remain unchanged.

The correction's [viewerNavigation](https://github.com/stef-k/Wayfarer/blob/c79ebfde810e0d8da32019c63aa174da9824338a/wwwroot/js/Trip/viewerSidebar.js#L7)
reads Leaflet's retained canvas. Each margin is `min(60, axis / 4)`. It uses the
rounded half-sidebar offset only when the remaining horizontal budget is at least
four margins; otherwise that offset is zero and navigation uses overlay fallback.
Thus positive canvases retain at least `min(120, width / 2)` usable fit width and
half their height. The check uses the rounded offset, including odd widths, so the
fit area and camera offset agree at the split/overlay boundary.

For the original 1280/1260 case, the offset becomes zero, padding becomes 60px per
side and the horizontal fitting area is 1160px. The supported Custom preference
and rendered sidebar width remain 1260px. A nearly covering opaque panel can hide
the fitted route until Hide reveals it; that is the documented supported behavior.

| Entry point | Reviewed owner and result |
|---|---|
| Initial/default camera | `tripViewerHelpers.initLeaflet` sets the initial view; the sidebar applies one integer offset, or zero for overlay fallback. |
| Camera permalink | `createViewerSidebar` initializes its applied offset from the same budget when lat/lon exist, avoiding a second initial pan or refit. |
| Explicit recenter | `Viewer.js:215` calls `centerFor` at the destination zoom, using the live budget. |
| Place details, markers and journey Place | `Viewer.js:359` opens the retained details pane before `centerFor`; both panes share the current width. |
| Segment row | `Viewer.js:398` calls the common selection controller; its separate fitting calculation was removed. |
| Selection button and route hit | The button calls that controller; the existing hit listener in `tripViewerHelpers.js:260` calls its published selection owner. |
| Requested Segment | Controller initialization calls the same selection/fit path for normal URLs. |
| Requested Segment print | Controller `fitBounds` receives the same bounded margins, with zero sidebar offset and animation disabled. |
| Hide/Show, presets, drag/keyboard, details and responsive resize | Sidebar render invalidates Leaflet size before reading the canvas, applies only the integer offset delta and never refits the Trip or persists a viewport clamp. |

The finite policy is proportionate. It consolidates the duplicated row fitting
at the existing selection owner without adding a navigation framework. No Editor
runtime, shared preference runtime, permissions, API, database, mobile, release,
deployment or CI configuration changes occur in the correction.

## Browser evidence and precision adjudication

The correction's [actual observations](evidence/correction-route.json)
record both real selection-button and row paths at zoom 13 in a 1280x793 canvas.
Each contains the original route inside the margins with finite center/zoom. After
the first 0.2813px center quantization, all six observations from three Hide/Show
cycles are exactly stable and retain route containment. A physical route-hit click
was not separately qualified; its common production callback was source-reviewed.

The first correction browser run passed the main Viewer journey, including details,
responsive geometry, drag/keyboard, reversible offsets, reload, embed and print.
Its focused test failed exact lat/lon equality after invalidation. The single
focused rerun passed the route and drift checks and recenter, then failed the final
Place assertion: 13.02486210292246m against a 5m threshold. Both complete focused
executions remain **FAIL**; the focused test is **partial evidence**, not green.

Independent inspection of installed Leaflet 1.9.4 confirms that `invalidateSize`
clears `_lastCenter`, while `_getNewPixelOrigin` rounds the projected pixel origin.
Recomputing that rounding for the recorded Place, canvas and zoom yields exactly
13.02486210292246m and 0.43284780285556973 CSS pixels; see
[independent arithmetic](evidence/independent-rounding.json). This explains the
observed difference without an unexpected sidebar offset or cumulative drift.

The final two center assertions correctly use projected CSS-pixel distance below
one pixel. That tolerance admits the subpixel origin rounding while still rejecting
the erroneous hundreds-of-pixels sidebar offset. Reversing only those measurement
changes and their comment reconstructs the focused managed spec's exact SHA-256;
see [delta verification](evidence/focused-spec-delta.json).

Those final assertions have **not been browser-rerun**. Their execution remains
bounded validation debt. The source, exact observed arithmetic, successful route
observations and stable lower-seam checks resolve the material product concern;
another managed browser build is unnecessary for this review. No browser campaign
or environment rebuild was launched in this session.

## Independent exact-head checks and reuse

| Check | Result |
|---|---|
| Focused client navigation/presentation tests | 16 passed; [log](evidence/client-focused.log). |
| Complete existing client suite | 522 passed, zero failed/skipped; [log](evidence/client-all.log). |
| Viewer/PublicTrip .NET selection | 130 passed, zero failed/skipped, with documented Chromium cache attached; [log](evidence/dotnet-viewer.log). |
| Vue typecheck | PASS; [log](evidence/typecheck.log). |
| Vite build | PASS; existing large-chunk advisory retained; [log](evidence/build.log). |
| Built asset smoke | PASS for Trip and four Razor entries/imports; [log](evidence/assets.log). |
| Complete-branch Code Guard | REVIEW only; no FAIL, INCOMPLETE, tool errors or policy relaxation; [JSON](evidence/guard-branch.json). |
| Diff hygiene and detached checkout | PASS; clean exact head. |

The .NET filter was
`FullyQualifiedName~TripViewer|FullyQualifiedName~PublicTrip|FullyQualifiedName~Viewer`.
The test invocation built the application and test dependencies successfully.
Existing unrelated compilation warnings remain; no full .NET-suite-green claim is made.

[Provenance verification](evidence/provenance.json) independently hashes the current
runtime against both managed correction-run inventories. All three changed runtime
files match. The final spec differs as documented above. Editor App, layout, styles,
shared CSS and shared preference runtime match the previous independent inventory;
the Editor journey body is unchanged in the exact correction delta. Its previously
accepted Notes instance/range, zero layout writes, map continuity, expanded/docked
restoration and explicit conditional PATCH/API readback are applicable without rerun.
The prior independent main Viewer PASS remains retained; the affected main journey
also passed freshly in the correction packet. These are reused rendered results,
not newly executed browser results from this review session.

All original custody hashes match. The original product failure, probe, JSON and
failed logs remain intact; the [closing comparison](evidence/custody-final.json)
confirms all 53 inventoried packet/custody files remain byte-identical.
Managed failure logs also retain successful fixture cleanup,
independent cleanup verification and environment verification.

Actual 200% browser-content zoom, a complete locale/theme matrix, separate anonymous
and private Viewer journeys, rendered Editor map-work restoration and a complete
exported print artifact remain outside the observed qualification. Print fitting is
covered at the controller seam, including short canvases; the main rendered print
controls passed. These limits do not establish a remaining reachable blocker for
the finite correction.

## Code Guard disposition

Agent Code Guard 0.5.1 ran with repository configuration, first on working changes
and then on the full branch against `main`, which resolves to the verified PR base.
Every REVIEW was inspected and accepted:

- App.vue 551 LOC and styles.css 567 remain within existing allowances. Viewer.cshtml
  897 decreases within its allowance; Viewer.js is 598, down from 608 at the previous
  reviewed head. Their existing view/application/style boundaries remain coherent.
- Unchanged App.applyMutation 95, Viewer.init 526 and retained export callbacks 90/85
  remain existing orchestration. The depth-5 finding is unchanged Place permalink
  handling at Viewer.js:652, outside the correction. No artificial extraction is warranted.
- createViewerSidebar 136 keeps one finite DOM/layout lifecycle and private event
  closures together. The small pure navigation budget is independently testable.
- The main Viewer journey 93 and focused route regression 99 are bounded linear
  journeys using the established managed host, with no role/viewport cross-product
  or new backend harness.
- CHANGELOG.md 1747 and its Changed/Fixed sections 305/270 remain the established
  release-history structure. The scoped entries do not justify restructuring it.

Only the named LOC, callable-size, nesting and Markdown-size policies were loaded.
No thresholds, exclusions, baselines or guard policy were changed.

## Delivery boundary

At the closing GitHub check (2026-11-04 19:14 UTC), the head/base remained unchanged,
PR #968 was draft/open/unmerged and issue #961 was open. Run `41894018386` had
successful classify and application-image jobs; test was still in progress and no
successful ci-gate was present. See [PR snapshot](evidence/live-pr-final.json).
No CI cancellation, rerun or dispatch occurred. This records independent technical
review acceptance of this exact head. Successful exact-head CI and maintainer
acceptance remain required before merge or closure; no merge permission is inferred.
