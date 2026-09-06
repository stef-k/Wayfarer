# Versioning

`Version.props` is the runtime and release version source for this slice. The root
file contains the manually edited `WayfarerVersion` value and maps the standard
MSBuild metadata directly from it:

```xml
<WayfarerVersion>1.9.13</WayfarerVersion>
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
output. The app writes exactly `Wayfarer 1.9.13`; `--no-launch-profile` avoids
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
