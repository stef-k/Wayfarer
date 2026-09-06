# Versioning

`Version.props` is the runtime and release version source for this slice. The root
file contains the manually edited `WayfarerVersion` value and maps the standard
MSBuild metadata directly from it:

```xml
<WayfarerVersion>1.9.11</WayfarerVersion>
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
output. The app writes exactly `Wayfarer 1.9.11`; `--no-launch-profile` avoids
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

## 1.9.11 local release review record

Prepared on 2026-09-06 on `feature/release-1.9.11` for independent review.
The clean starting HEAD, local `main`, and fetched `origin/main` matched
`1cacd3fba1439c039668a66e4312cafe9be53550`.
GitHub identified `v1.9.10` as the latest published, non-prerelease release;
its tag resolves to `7ae4b2c78b93dea27e77615c46361caf6e94bfd7`.
The first-parent range `v1.9.10..1cacd3fba1439c039668a66e4312cafe9be53550`
contains exactly one commit:

| PR / issue | First-parent commit | Scope |
| --- | --- | --- |
| [#578](https://github.com/stef-k/Wayfarer/pull/578) / #577 | `1cacd3fba1439c039668a66e4312cafe9be53550` | Wider Segment direction chevrons in the Editor and Viewer, mirrored geometry assertions and changelog |

### Migration boundary and compatibility

There are no migration or model snapshot changes since `v1.9.10`.
The latest migration remains `20260905095140_AddLocationProviderAddressLine1`;
it adds nullable `Locations.ProviderAddressLine1` with maximum length 500.
This patch adds no API, persistence, dependency or routing-provider changes.
Upgrades from older releases still need their pending migrations. Preserve
PostgreSQL and its matching Data Protection keys. No database update was run
as part of release preparation; production migration state is not asserted.

### Product evidence and limits

The reviewed source commit `7aa3e4d9fdc919bc4bdee85434e12bc6448955e8`
was preserved on the local fix branch and merged through the normal squash workflow.
Before merge, 17 geometry/parity tests, frontend typecheck, production build and
built-asset smoke passed in fresh local runs. Vite retained its large-chunk advisory.
The supplied review handoff includes synthetic Viewer evidence; that observation
was not rerun during PR creation. Full-page Viewer and print visuals remain untested.
The width-only change preserves tangent lengths, placement, direction and styling.

GitHub Actions [Tests run 34039660952, test job 101503989599](https://github.com/stef-k/Wayfarer/actions/runs/34039660952/job/101503989599)
completed successfully on that exact PR head before merge. This is product-PR CI,
not release-branch CI or proof of the unobserved full-page/print visuals.

### Local validation and handoff

Release-helper preparation and offline check passed. Helper tests passed all 21
cases using `python -m pytest tools/release/tests -q` (unittest discovery found no
cases, so it supplies no test evidence). Focused .NET Versioning tests compiled
the app and test project and passed all 26 cases with no failures or skips.
`dotnet run --no-launch-profile --no-build -- version` returned exactly
`Wayfarer 1.9.11`. A text comparison verified prior released notes unchanged,
one empty Unreleased section and no release placeholders. Whitespace checks passed.
Changed-work and complete branch Code Guard reported only the 875-line changelog
size REVIEW, accepted because chronological release history remains a cohesive,
navigable document. All other guards passed; no FAIL or INCOMPLETE.
The release diff is limited to `Version.props`, `CHANGELOG.md`, this document,
and the compiled-version assertion in `AppVersionProviderTests.cs`.
The #577 note moves into the dated release, prior released notes remain unchanged,
and exactly one empty Unreleased section remains below the new release.

Stop at a local checkpoint for independent review against the verified base.
Release-branch PR creation, CI, merge, tagging, publication and deployment remain
outstanding. No tag, release publication or deployment is authorized by this preparation.
