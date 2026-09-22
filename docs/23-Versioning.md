# Versioning

`Version.props` is the runtime and release version source for this slice. The root
file contains the manually edited `WayfarerVersion` value and maps the standard
MSBuild metadata directly from it:

```xml
<WayfarerVersion>1.9.18</WayfarerVersion>
<Version>$(WayfarerVersion)</Version>
<PackageVersion>$(WayfarerVersion)</PackageVersion>
<AssemblyInformationalVersion>$(WayfarerVersion)</AssemblyInformationalVersion>
<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
```

The running app reads `AssemblyInformationalVersion` from the compiled Wayfarer
assembly through `IAppVersionProvider`. Runtime surfaces such as
`dotnet run --no-launch-profile -- version`, `GET /api/version`,
`X-Wayfarer-Version`, and the shared layout footer use that provider instead of
separate constants.

Use `dotnet run --no-launch-profile -- version` when validating exact CLI
output. The app writes exactly `Wayfarer 1.9.18`; `--no-launch-profile` avoids
.NET SDK launch-profile messages so validation stays focused on app output.

## Release helper

Use the repo-local helper to prepare and validate release metadata:

```powershell
python tools/release/version.py prepare <next-version>
python tools/release/version.py check
python tools/release/version.py check --require-tag
python tools/release/version.py check --require-github-release
```

`prepare <next-version>` updates only `WayfarerVersion` in `Version.props` and
adds the required changelog skeleton for the target release. The default `check`
command validates only offline repo files; tag and GitHub release checks run
only when their explicit flags are supplied. The helper validates release state
but does not create, edit, publish, or delete GitHub releases.

## 1.9.18 release source record

Prepared on 2026-09-22 from synchronized main `cd35db48`.
The only change since v1.9.17 is [PR #602](https://github.com/stef-k/Wayfarer/pull/602),
closing #601: public Timeline and embed statistics share public-point privacy
eligibility, including delay and Hidden Areas, before sampling or aggregation.
Private owner statistics remain unchanged; empty public history has zero counts
and empty dates. Existing timestamp conventions remain documented in
[Timeline statistics](06-Timeline.md#statistics-grouping).

Product evidence retained from PR #602 includes independent review and 104
focused passing tests; its exact final-head GitHub Actions `test` check passed.
Release validation covers helper tests, compiled Versioning tests, exact CLI
output, preservation of prior release notes, diff hygiene, and Code Guard.
Product/browser suites are not repeated locally for metadata preparation.

No database migration, API shape, dependency, or Mobile changes since v1.9.17.
Follow the [server-build deployment workflow](20-Deployment.md#updating-wayfarer)
and reload open pages. Older upgrades still apply pending migrations; preserve
PostgreSQL and its matching Data Protection key ring. GitHub publication does
not deploy the server.

## 1.9.17 release source record

Prepared on 2026-09-17 from main `e6b8bd48` with the shared embed cursor
correction on `fix/embed-full-view-cursor`. Both public Trip and Timeline
full-view links explicitly use the pointer cursor. Existing Trip URL and iframe
copy actions remain unchanged; the maintainer confirmed they are visible.

Validation covers the five shared embed client tests, release helper tests,
compiled Versioning tests, exact CLI output, diff hygiene, and complete-branch
Code Guard. No fresh mounted-browser hover verification is claimed.
The changelog remains a cohesive release history despite its size review.

No database migration, API, dependency, or Mobile changes since v1.9.16.
Follow the [server-build deployment workflow](20-Deployment.md#updating-wayfarer)
and reload open pages. Older upgrades still apply pending migrations; preserve
PostgreSQL and its matching Data Protection key ring. GitHub publication does
not deploy the server.

## 1.9.16 release source record

Prepared on 2026-09-15 from synchronized main
`4feebd2773008d6b5c67378c540939c0368c6cd1` on `feature/release-1.9.16`.
The first-parent delta since v1.9.15 contains only
[PR #597](https://github.com/stef-k/Wayfarer/pull/597), closing #596:
shared cooperative map embeds, accessible full-view escape, canonical URL/HTML
sharing, Timeline iframe sizing, and thumbnail escape suppression.

Independent product review and focused thumbnail re-review passed at
`e107451a47fadd31e0cba368b35ed8a64834fe43`. The exact-head
[test check](https://github.com/stef-k/Wayfarer/actions/runs/35014379085) passed.
Retained evidence includes three mounted iframe browser tests, Chromium mobile
emulation, and 20 thumbnail plus five shared embed tests in the final re-review.
The thumbnail browser test uses real Leaflet and production capture code with
fixture HTML; it does not establish a live-database journey. Physical-device and
Safari acceptance are not claimed. Release metadata preparation does not repeat
these product/browser suites.

No database migration, API, dependency, or Mobile changes since v1.9.15.
Existing embed URLs remain valid; copying embed output requires public HTTPS.
Follow the [server-build deployment workflow](20-Deployment.md#updating-wayfarer)
and reload open pages after deployment. Older upgrades still apply pending
migrations; preserve PostgreSQL and its matching Data Protection key ring.
Release validation covers helper tests, focused Versioning tests, exact CLI
output, changelog preservation, diff hygiene, and Code Guard. Publication does
not perform server deployment.

## 1.9.15 release source record

Prepared on 2026-09-14 from synchronized main
`44ed5356edb68fc2427479643455084350b3049c` on `feature/release-1.9.15`.
Latest published release v1.9.14 resolves to
`6a6251506110ba95c6bdaf7179249801ed0b16cb`. The first-parent delta is:

- `a790b001`: [PR #591](https://github.com/stef-k/Wayfarer/pull/591), closes #590; provisions `/var/log/wayfarer` for `APP_USER` with mode `0750` before service startup.
- `44ed5356`: [PR #593](https://github.com/stef-k/Wayfarer/pull/593), closes #592; restores direct pan/zoom capture into the Trip default-view draft and persistence through existing Save, with partial URL overrides and URL/history preservation.

Toolbar commands and map-work remain transient; Reset retains the live viewport.
Newer captures survive unrelated refreshes and delayed saves, including captures
matching manually entered values. No database migration, backend API, dependency,
or Mobile changes since v1.9.14. Reload open Trip Editor pages after deployment
and follow the existing [server-build workflow](20-Deployment.md#updating-wayfarer).
Older upgrades still apply pending migrations; preserve PostgreSQL and its
matching Data Protection key ring.

Both product PRs have successful exact-head `test` CI:
[#591 run](https://github.com/stef-k/Wayfarer/actions/runs/34384832416) at
`fb44dd13fc18d9b17f6a8593a2d385d2dfca851e` and
[#593 run](https://github.com/stef-k/Wayfarer/actions/runs/34827297292) at
`9b9a0c64df9e94cb3b0499313d27b82e94b803c9`.
For #592, retained evidence includes complete independent review plus passing
narrow P2 re-review and eight focused tests after correction. All 10 browser
cases passed across runs; seven retain earlier evidence. Browser/build artifacts
predate the narrow correction. Retained persistence evidence covers real PATCH,
API reread, and clean-URL reload. Physical touch hardware was not exercised.
The #590 service-start operational evidence belongs to PR #591; this preparation
does not perform or claim a fresh production deployment.

Release-source validation covers the helper and its tests, app build, focused
Versioning tests, exact CLI output, prior-note preservation, whitespace, and
Code Guard. Product/browser suites are not repeated for metadata preparation.
Independent release-source review and exact-head release-PR CI come next;
tagging, publication, deployment, and migrations remain separate stages.

## 1.9.14 release source record

Prepared on 2026-09-07 from synchronized main `a45355cc` on
`feature/release-1.9.14`. The delta since v1.9.13 contains only
[PR #588](https://github.com/stef-k/Wayfarer/pull/588): saved Segment dirty-state
correction, removal of unused parent state, and one custom confirmation for
app-controlled exits. Browser unload approval is consumed once so interrupted
navigation retains protection.

There are no migration, API, dependency, or Mobile changes. Reload open editors
after deployment using the existing server-build source-release workflow.
Older upgrades still apply pending migrations; preserve PostgreSQL and its
matching Data Protection key ring.

Independent review passed at `b8a75ff799aee319df01e4150c1c250037332187`.
Nine focused client tests, frontend typecheck/build and exact-head correction
[test CI](https://github.com/stef-k/Wayfarer/actions/runs/34133148745) passed.
The focused tests exercise production component state and exit guards; no
mounted-browser Save-and-leave journey is claimed.

Release validation covers the release helper and its tests, compiled Versioning
tests, exact CLI version output, prior-note preservation, whitespace and Code
Guard. Release PR CI and publication are separate gates. Server deployment
remains with the maintainer.

## 1.9.13 release source record

Prepared on 2026-09-06 from synchronized main `fd58c69870b9e528580abadf3f3a43f240f5cc4f` on
`feature/release-1.9.13`. Latest published release verified as v1.9.12.
Its first-parent delta contains only PR #585 (#584 atomic proposal Save) and
PR #586 (#583 preview, independent provider mode and display refinements).

Migration files and snapshot are unchanged since v1.9.12. The latest migration
remains `20260905095140_AddLocationProviderAddressLine1`. Preserve PostgreSQL
and the matching Data Protection key ring; older upgrades still apply pending
migrations. Deployment follows the existing server-build source-release workflow.
The removed acceptance endpoint is replaced by ordinary validated Segment Save;
reload open editors after deployment. No Mobile protocol or release changes.

Independent product reviews covered the Save correction, preview, planning-mode
independence and contrast changes. Maintainer-directed final tuning uses 80%
current-route opacity, flat preview dash ends and at most two displayed decimals.
The maintainer observed real proposals and accepted the appearance. Full mounted
dark-theme, Discard and post-Save observation is not claimed. Existing lower-seam
coverage and independent reviews established no blocking risk; the earlier
owned-Trip fixture 404 did not establish a Vite integration defect.

Final correction selection: 24 client tests, frontend typecheck/build and built
asset smoke passed. Retained backend/PostgreSQL selections remain attributed in
PRs #585/#586, not summed. Exact correction-head test CI passed at
`aceafe46a10dc46271f426e8b3b5823f35c668ca` in run 34058012120.
Release metadata validation uses the helper, 21 helper tests, 26 compiled
Versioning tests, exact CLI output, prior-release-note preservation, whitespace
and complete-branch Code Guard. No new provider request or production operation
is part of this preparation. Release PR CI and publication are separate gates.

## 1.9.12 release source record

Prepared on 2026-09-06 from synchronized `main` at
`182665fa4bcd1431540f737063b428b98c23b898` on `feature/release-1.9.12`.
The latest published release was `v1.9.11`, whose tag resolves to
`f3934de632aca388c345cc54f7a312d97978be52`.
The first-parent range from that tag to the preparation base contains only:

| PR / issues | First-parent commit | Scope |
| --- | --- | --- |
| [#581](https://github.com/stef-k/Wayfarer/pull/581) / #580, #577 follow-up | `182665fa4bcd1431540f737063b428b98c23b898` | Thicker Segment chevron strokes and suppression of locally contradictory direction cues |

### Migration boundary and deployment

Migration files and the model snapshot are unchanged since `v1.9.11`.
The latest migration remains `20260905095140_AddLocationProviderAddressLine1`.
There are no API, persistence, dependency or routing-provider changes.
Upgrades from older releases still require their pending migrations; preserve
PostgreSQL and its matching Data Protection keys.
Use the existing [server-build deployment workflow](20-Deployment.md#updating-wayfarer)
from the tagged source. This release does not introduce a binary asset workflow.
Deployment and any required migrations remain with the maintainer.

### Product evidence and limits

Reviewed commit `1bce6a75d089a708733047d6415ec0c6817539d7` remains preserved
on the local correction branch. Independent combined review passed, and the
maintainer accepted sizing and local direction during zoom checks.
The supplied review evidence records 34 passing focused client tests, frontend
typecheck/build and built-asset smoke. The historical 6,308-vertex Ella-to-Kandy
replay remains independently unverified because its artifact was unavailable;
mirrored synthetic tight-return tests provide the reproducible direction proof.

GitHub Actions [test job 101515334540](https://github.com/stef-k/Wayfarer/actions/runs/34043873977/job/101515334540)
completed successfully on that exact reviewed PR head before the normal squash merge.
This is correction-PR CI; release-PR CI is separately required before publication.

### Release validation scope

Release validation uses the helper and its tests, focused .NET Versioning tests,
exact CLI output, prior-note preservation, whitespace and complete-branch Code Guard.
Product suites are not repeated locally for metadata-only preparation.
The release diff is limited to `Version.props`, `CHANGELOG.md`, this document,
and the compiled-version assertion in `AppVersionProviderTests.cs`.
Both correction notes move into the dated release, with prior released notes
preserved and one empty Unreleased section retained.
