---
title: Rich Notes reconciliation
---

# Rich Notes reconciliation

Issue [#939](https://github.com/stef-k/Wayfarer/issues/939) replaces the retired
Quill implementation with one native Tiptap 3.32.2 engine. W1 and W2 are accepted;
W3 removes the remaining distribution and compatibility paths. Final combined
independent contract-to-code review, current-head CI and maintainer acceptance
remain separate gates. This document records the resulting backend/Web contract.

## Current HTML and transition

Notes remain nullable HTML in the existing fields and transports. The single
editing engine is `ClientApps/rich-notes/editor.ts`; Razor uses `razor.js` and Vue
uses `RichNotesEditor.vue`. Read-only views do not mount an editing engine.

Keep paragraphs, breaks, H1–H6, blockquotes, Unicode, meaningful whitespace,
bold/italic/underline/strike, semantic ordered/bullet/nested lists, safe links
and HTTP(S) images. Only `font-family: serif|monospace` on spans and
`text-align: left|center|right|justify` on paragraphs/headings survive as CSS.
For example:

~~~html
<h2 style="text-align: right;"><span style="font-family: serif;">A visit</span></h2>
<ul><li><strong>Remember</strong> <a href="/safe">this place</a></li></ul>
~~~

Classes, list metadata, arbitrary attributes/styles and active resources receive
no legacy translation or special content deletion. Unsupported historical
formatting may change on display or deliberate editing; users may repair it.
No historical-format converter, fallback editor, bulk Notes rewrite or migration
is introduced. Opening/focusing is not an authored change. Undo to the initial
document retains the initial input where the feature contract supports it.
Ordinary MVC still posts its retained published input through server normalization;
it has no new durable recovery or raw-storage-byte preservation promise.

Server sanitization and URL validation remain authoritative. Image HTML retains
canonical external URLs; live display uses the secure proxy before insertion.
Never retry a failed image directly against its origin. File/data image ingress
is rejected with feedback; failed saved HTTP(S) references remain repairable.
Security checks remain even when they predate the editor replacement.

## Consumer and capability matrix

Every authoring row below uses the same native engine and command/transfer policy.
All have labelled controls, URL dialogs with Escape/focus return, live undo/redo,
native rich/plain paste and lists, and Remove formatting retaining text/links/images.
Remove formatting clears visual marks/font and applicable block/alignment styling;
at a caret it clears pending visual marks. Explicit Clear is a separate action.

| Consumer | Controls and capabilities | Save, original and reset responsibility | Evidence |
| --- | --- | --- | --- |
| Location Create | Fonts, H1–H6, marks/lists/link/image, Clear | Ordinary MVC Notes and validation input; mounting does not change the field | W2 accepted; fresh Razor/native tests |
| Location Edit | Same Location toolbar | Protected form owns exact attempt bytes, originals, Unchanged/Set/Clear, newer drafts, reload and Unknown holds; unrelated saves omit Notes | W1 accepted smoke; fresh client, native and PostgreSQL unrelated-save tests |
| Location Bulk Notes | Same Location toolbar | Filters/count remain advisory; Apply selects current scope; Append/Replace and ClearNotes checkbox remain independent | W2 accepted; fresh Razor and existing controller seams |
| Visit Edit | H1–H3 toolbar, strike, marks/lists/link/image; loaded H4–H6 remain editable | Independent NotesHtml snapshot and ordinary MVC validation | W2 accepted; fresh Razor and backend Notes tests |
| Trip metadata | Trip fonts, alignment, marks/lists/link/image | Metadata draft/nullable original/immutable attempt/acknowledgement/Unknown/exit and Notes reset session | W2 accepted hosted smoke; fresh native owner/client tests and PostgreSQL original regression |
| Region | Shared Vue Trip controls | Conditional scalar/create owner; ordering originals independent; reset retires only this Notes history | W2 accepted; fresh native owner/client and parser tests |
| Place | Shared Vue Trip controls | Scalar versus structural/move/coordinate effects, parent proof and deletion recovery stay owned | W2 accepted; fresh native owner/client and parser tests |
| Area | Shared Vue Trip controls | Geometry/parent proof and Notes remain independent; reset/adoption keyed to owner | W2 accepted; fresh native owner/client and parser tests |
| Segment | Shared Vue Trip controls | Notes-only versus atomic route saves, exact original, route proof, Clear consent, uncertain holds and retired sessions remain owned | W2 accepted; fresh native owner/client and existing conditional parser seams |
| Private/public Trip, Timeline/Groups, previews, readable/print/PDF | Sanitized semantic HTML, finite inline CSS, proxied images; no editor instances | Publication is a derived copy; reading does not persist normalized HTML | Fresh display/CSS and backend publication tests; unchanged accepted host/PDF integration reused |
| Import/export | Existing nullable Notes fields, native KML and existing Location formats | Existing ownership/privacy/schema and ingress/egress normalization | Fresh existing native all-five-domain import/export round trip and Location transport tests |
| Assets | Four Razor entries and one Trip entry in the existing Vite graph, including imported CSS | Each page mounts once; layout no longer loads a default editor | Fresh builds, five-entry asset checks and clean Release publish inventory |

Trip metadata, Region, Place and Area request parsers now preserve submitted
expected Notes exactly. Only authored replacements use current normalization.
An original published under older sanitizer rules must not be sanitized into a
different proof that matches current publication. Existing Segment exact-original
and Location receipt/fingerprint/comparison contracts remain owned by those
features. Nothing rewrites frozen attempts to make an acknowledgement succeed.

## Removal and remaining-reference disposition

Normal `npm uninstall quill` removes eight lock nodes: the direct package,
quill-delta, parchment, eventemitter3, fast-diff, lodash-es, lodash.clonedeep and
lodash.isequal. No other locked package changes; Tiptap's exact pins/licenses
remain unchanged. Run `npm ci`, `npm ls --all` and `npm audit --json` for the
current dependency closure and advisory evidence; absence is not a claim about
every future upstream advisory or another installed release.

The copied `wwwroot/lib/quill/` JavaScript, map, Snow CSS and bundled notice are
removed. Their top-level notice is removed because that material is no longer
distributed. No derived styling is retained to justify obsolete attribution.
Layout tags, all LoadQuill switches, vendor class/list allowlists and UI-span
deletion, the image-blot registration, unused old image-input/save helpers and
editor/theme/inline-color compatibility CSS are removed. Standard image sizing,
native editor styles, semantic read-only CSS and security helpers remain.

Every remaining source reference has one of these explicit dispositions:

| Path | Disposition |
| --- | --- |
| `CHANGELOG.md` | Chronological implementation/retirement record; older pin and compatibility statements are historical |
| `docs/development/dependency-servicing-r1.md` | Preserved R1 assessment, advisory failure and review record, explicitly superseded for current editor installation |
| This document | Retirement inventory and final contract record; package/path names describe removals |
| `docs/development/testing.md` | Historical explanation of superseded hosted parity matrices; no old installation/download instructions |
| `docs/maintainer/history-preparation.md` | Concrete changed-extraction example and controlled text-generation invalidation |
| `tests/Wayfarer.Tests/Models/EditorRichNotesRequestHtmlTests.cs` | Negative class/list-attribute removal and exact frozen-original regression inputs |
| `tests/Wayfarer.Tests/Controllers/RichNotesHistoricalPublicationTests.cs` | Historical rows publish safely without mutation |
| `tests/Wayfarer.Tests/Controllers/TripMetadataConditionalPostgresTests.cs` | Unrelated-save storage preservation and rejection of a transformed frozen base |
| `tests/Wayfarer.Tests/Controllers/LocationMutationPostgresTests.Values.cs` | Non-mutating publication/unrelated protected save and retained request bytes |
| `tests/Wayfarer.Tests/Services/LocationHistoryDerivationTests.cs` | Concrete new-generation text-extraction counterexample |
| `tests/client/locationMutation.test.mjs` | Historical original fixture proves unchanged, deliberately empty and Clear remain distinct |
| `tests/editor/consumerNotes.test.mjs` | All five actual Vue owners preserve frozen historical originals while draft HTML uses current normalization |
| `tests/editor/razorNotes.test.mjs` | Already-published validation input remains untouched until authored editing |
| `tests/editor/richNotes.test.mjs` | Accepted native irregular-list observation, untouched/undo proof and negative attribute interpretation regression |
| `tests/e2e/shared-layout/locationTiptap.spec.ts`, `tripTiptap.spec.ts` | Negative assertions that retired script tags are absent; no retired runtime is loaded |

Fresh publish inspection also finds one unrelated uppercase `QUILL` word inside
Playwright's unchanged package-owned Node binary, alongside Unicode-name data.
It is not an editor module or executable distribution; retain the rendering driver.
The binary SHA-256 matches the installed Microsoft.Playwright 1.64.1 payload.

No executable editor dependency, active loader, translation branch or production selector
is an allowed remaining reference. Search tracked text with word boundaries so
PostgreSQL package names are not mistaken for editor selectors. Inspect filenames,
copied maps and compressed publish variants as well as visible source text.

## History text and validation boundaries

Removing vendor-specific span deletion changes `RichNotes.ToPlainText` for content
inside a formerly special UI span. `history-text-2` therefore replaces the previous
R1 text ID. Existing controlled history preparation must rebuild derived text
before activation; time, qualification and canonical Notes remain unchanged.
See [history preparation](../maintainer/history-preparation.md). Source cleanup
does not run an operational rebuild, deployment or release.

Use [the focused test commands](testing.md#native-rich-notes-editors-w1w3).
W3 requalifies changed normalization, exact originals, standard rendering, image
policy, current owners and transport at their existing stable seams. Accepted
W1/W2 hosted interactions, proxy integration and unchanged owner evidence can be
reused with a source-delta explanation. Native DOM command/component tests use
the existing Chromium seam; no new broad browser/database/mobile/performance
campaign or full-suite-green claim is implied.
