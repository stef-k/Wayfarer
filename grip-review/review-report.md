# Independent grip-delta review: PR #968 / issue #961

**Verdict: technical PASS with retained validation limits. No blocking findings.**

Reviewed head: `b90d6cfc17fcbee0f49b8a0ba10d9f6ac0b42427`.
Verified PR base: `31303f7b2ab4ac6ca347f0c98c1d2356841fefdc`.
Previous independently accepted head: `c79ebfde810e0d8da32019c63aa174da9824338a`.
Review date: 2026-11-04.

This session performed the review directly in the fresh isolated detached
checkout at `source/`. The live head/base matched before and after review.
The live issue, checkout AGENTS.md, implementation report, original appearance
handoff and previous independent review were read. The seven-file correction
was reviewed in full; the earlier feature campaign was reused within its scope.
No delegation, production edits, commits, pushes, PR/issue writes, CI mutations,
merge, closure, release or deployment occurred.

## Appearance and preserved behavior

The only production changes are
[Editor separator styles](source/ClientApps/trip-editor/src/components/TripSidebarLayout.vue:102)
and [Viewer separator styles](source/wwwroot/css/trip-sidebar.css:6).
Both remove the previous full-height neutral border and introduce the same
centered, rounded 3 x 24 CSS-pixel pseudo-element, with opacity .55 normally
and .85 on hover or keyboard focus. Both use the existing neutral theme color;
the Editor token resolves to Bootstrap's secondary color. The pseudo-elements
have `pointer-events: none` and introduce no DOM or focus target.

I inspected all twelve final 1280px captures: normal, hover and keyboard focus
in actual light/dark themes for both runtimes. The short marks are recognizable
but quiet, hover emphasis is restrained, and the two runtimes are visually
consistent. Both rendered observation files record the same treatment:

| Property | Light | Dark |
|---|---|---|
| Theme-derived color | `rgba(33, 37, 41, 0.75)` | `rgba(222, 226, 230, 0.75)` |
| Normal opacity | .55 | .55 |
| Hover opacity | .85 | .85 |
| Grip dimensions | 3 x 24px | 3 x 24px |
| Decorative pointer events | none | none |

The final tests operate the production navbar toggle and assert the body's
`data-bs-theme`, which is the real theme owner. The screenshots also visibly
confirm the theme change. Normal and hover states have no full-height colored
bar. Keyboard focus retains the pre-existing 3px accent outline around the
full-height 8px separator; that focus-only outline is deliberately preserved
and is clearly visible in all four focus captures. It is distinct from the
short decorative grip. No animation, oversized handle or persistent label was added.

The 8px target, col-resize cursor and touch-action remain. Script/template bytes
are unchanged, preserving separator semantics, live ARIA dimensions, 420px
minimum, keyboard behavior, pointer capture/cancellation and focus ownership.
The same compact/phone, hidden, Viewer print/embed and Editor surface-owner
visibility rules continue to govern the existing handle. The decoration cannot
escape its hidden parent or add an independently interactive surface.

The final retained browser selection passes ArrowRight from 500 to 520px,
dragging from the actual centered grip to 600px under pointer capture, Custom
selection, Hide/Show and 900px compact exclusion in both runtimes. Viewer print
exclusion also passes. See the [managed run log](../wayfarer-968-grip-20261104/browser.log)
and [final captures and observations](../wayfarer-968-grip-20261104/screenshots/).
These are implementation-run observations independently inspected here, not a
new browser execution by this reviewer. No unresolved product concern warranted
another rendered run or environment rebuild.

## Source provenance and evidence reuse

[Independent provenance checks](evidence/provenance.json) establish that:

- The exact delta has seven files: the two production styles, existing visual
  test file, user guide, changelog and two documentation images.
- The Vue script/template and all prior journey/helper bytes are identical to
  c79ebfde; the test delta only inserts the two focused grip observations.
- Viewer navigation and selection, shared resize/preferences, Editor App,
  styles and surface owner, and the Razor view match c79ebfde byte for byte and
  match the supplied source-reuse hashes. No permissions, API/database, layout
  persistence or draft owner changes occur.
- The current two style owners and test match both final managed hash inventories
  and their retained Git checkpoint `0212db4025e2f727c9c342a0f5fde9e99224fdf6`.
  Only the two documentation images differ after that checkpoint. The temporary
  managed source directory has been cleaned up, so its original bytes are bound
  through the retained inventories/checkpoint rather than re-read from that directory.
- Both committed documentation images exactly equal their final light captures.

The [previous independent review](../wayfarer-968-correction-review-20261104/review-report.md)
remains applicable to the unchanged navigation, draft, map and geometry owners.
Its logs confirm 522 client tests and 130 focused .NET tests passed; these counts
are reused, not fresh results at this head. The accepted rendered draft/Notes
instance and range, explicit conditional save/readback, layout geometry, map
continuity and navigation observations are likewise reused within their prior
limits. Managed application/fixture builds are retained implementation evidence;
no fresh .NET build or broad suite was necessary for this style-only correction.

## Fresh independent checks

| Check | Result |
|---|---|
| Sidebar width/navigation and dirty-state client selection | 5 passed, zero failed/skipped; [log](evidence/client-focused.log). |
| Vue typecheck | PASS; [log](evidence/typecheck.log). |
| Vite production build | PASS; [log](evidence/build.log). Existing large-chunk advisory retained. |
| Built asset smoke | PASS for Trip and all four Razor entries/imports; [log](evidence/assets.log). |
| Working-change Code Guard | PASS with zero selected files in the clean detached checkout; [JSON](evidence/guard-working.json). |
| Complete-branch Code Guard against verified `main` | REVIEW only; 21 selected, 13 applicable; [JSON](evidence/guard-branch.json). |
| Full-branch diff hygiene | PASS; detached checkout remains clean. |
| Final packet inventory | All 82 recorded hashes match; [verification](evidence/packet-inventory.json). |
| Historical evidence custody | All 55 original hashes match; [verification](evidence/original-custody-before.json). |

The focused dirty-state test retains its existing Vue inject warning while
passing; no new failure or broader green-suite claim is inferred.

Every Code Guard REVIEW was inspected and accepted under the named LOC,
callable-size, nesting and Markdown-size policies:

- Unchanged App.vue 551 and styles.css 567 remain cohesive application/style
  boundaries within their allowances. Viewer.cshtml 897 remains within 934;
  Viewer.js 598 remains below its 711 allowance. This delta does not grow them.
- Unchanged App.applyMutation 95, Viewer.init 526 and export callbacks 90/85
  retain the existing mutation, Viewer and download orchestration. The unchanged
  depth-5 finding is Place-permalink handling at Viewer.js:652, outside this delta.
- createViewerSidebar 136 keeps one DOM/layout lifecycle with private event
  closures. Its bounded navigation budget remains separately testable.
- The existing Viewer journey 93 and focused navigation regression 99 retain
  their exact bodies, merely shifted by the inserted observations. They are
  bounded linear journeys at the existing managed seam. The new tests add no finding.
- CHANGELOG.md 1748 and Changed/Fixed sections 306/270 retain the established
  release history. The one-line net growth documents the requested grip and
  does not justify restructuring the history.

There are no FAIL, INCOMPLETE, tool/configuration errors or policy-relaxation
findings. No thresholds, exclusions or baselines were changed.

## Retained failures, limits and delivery boundary

The first grip run remains FAIL because keyboard focus carried into the next
theme's normal observation. The second reports two test passes, but its Viewer
dark captures remain unqualified because the body theme did not change. The
explicitly authorized additional run supplies the qualified final captures.
No earlier run was relabeled. The original navigation F1 product FAIL and both
failed focused navigation executions also remain failures. Their final
CSS-pixel tolerance assertions remain unexecuted, as previously adjudicated.

Actual 200% browser-content zoom, complete locale/theme matrices, separate
anonymous/private journeys, rendered Editor map-work restoration and a complete
exported print artifact remain outside qualified evidence. The finite two-theme
grip observations do not close those broader limits. No full-suite-green,
deployment or production acceptance claim is made.

At the closing check, **2026-11-04 19:47 UTC**, live revisions were unchanged,
PR #968 remained draft/open/unmerged and issue #961 remained open. Exact-head
run `41899163868` had successful classify and application-image jobs; test was
still in progress and no successful ci-gate was present. See the
[PR snapshot](evidence/live-pr-final.json), [CI snapshot](evidence/ci-final.json)
and [closing verification](evidence/closing-verification.json).

All 139 protected packet/historical files remained unchanged during this review.
The shared checkout retains its original branch/head and original untracked
directories. The review checkout is retained clean at the reviewed head.

This report records independent technical acceptance of the seven-file grip
correction at b90d6cfc. Successful exact-head ci-gate and maintainer acceptance
remain required before merge or issue closure. No merge authority is inferred.
