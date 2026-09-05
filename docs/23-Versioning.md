# Versioning

`Version.props` is the runtime and release version source for this slice. The root
file contains the manually edited `WayfarerVersion` value and maps the standard
MSBuild metadata directly from it:

```xml
<WayfarerVersion>1.9.10</WayfarerVersion>
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
output. The app writes exactly `Wayfarer 1.9.10`; `--no-launch-profile` avoids
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

## 1.9.10 local release review record

Prepared on 2026-09-05 on `feature/release-1.9.10` for independent review.
The clean starting HEAD, local `main`, and fetched `origin/main` matched the
expected base `49ea22ec762409beeb999409e24bef872b0e2bee`.
Previous release `v1.9.9` resolves to `dd3d0b022e1308fa18ad7b87eae95357bfda5afd`.
The first-parent range `v1.9.9..49ea22ec762409beeb999409e24bef872b0e2bee`
contains exactly these two commits:

| PR / issue | First-parent commit | Scope |
| --- | --- | --- |
| [#574](https://github.com/stef-k/Wayfarer/pull/574) / #572 | `6ef7fe06532121d681a416e3e25b98b32b6bca62` | Geoapify field mapping, retained provider line, address presentation and backend round trips |
| [#575](https://github.com/stef-k/Wayfarer/pull/575) / #573 | `49ea22ec762409beeb999409e24bef872b0e2bee` | Consistent geographic grouping, missing-parent rendering and Greece-scoped region alias |

### Migration boundary and compatibility

The only added migration is `20260905095140_AddLocationProviderAddressLine1`,
after `20260904100416_RetireLegacyRoutingAuthority`. Its Up operation adds nullable
`Locations.ProviderAddressLine1` as `character varying(500)` without a default or
any data update. The range changes only its migration, designer and
`ApplicationDbContextModelSnapshot.cs` under `Migrations`; the snapshot adds the
matching optional string with maximum length 500. Existing rows are preserved.
Down drops the column and loses any retained provider lines stored there.

The user reports the development migration succeeded during this session. This
is local evidence only, not independently rerun here; production migration remains
outstanding. Preserve PostgreSQL and the matching Data Protection keys on upgrade.
The migration does not correct historical stored addresses or mixed fields;
repair remains fill-only. Mapbox mappings, Trip Place preference and released-Mobile
fields including `FullAddress` remain preserved. Released Mobile does not promise
retention of unknown fields through its own offline formats.

Statistics still group recorded labels, trimming only outer ASCII whitespace and
scoping by recorded parents. The sole explicit read-time alias maps
`East Macedonia and Thrace` to `Eastern Macedonia and Thrace` under exact `Greece`.
Stored labels remain unchanged. Same-named settlements under identical parents
remain indistinguishable; other aliases can split one entity. Parent scoping can
increase counts, while this alias correction can decrease them.

### Reused product evidence and limits

Live PR records and checks were read through `gh`. The `test` job succeeded on
#574 head `bd1ce140873ddc3a43180a679aca4228afae7dc7` and #575 head
`4356848d55b848b4fbb422113fdddbea8986f657`.
#574 records independent review and a successful retention-fix re-review, including
actual exporter-to-parser whitespace/provenance cases. Its reviewer selections
were 248 backend tests including PostgreSQL and 117 client tests before remediation,
then 115 focused backend cases at re-review. #575 records independent review with
35 service/controller tests including PostgreSQL, two renderer tests and fresh
in-memory bundling of both Timeline callers. These are attributed review records,
not fresh product reruns; selections overlap and are not added together.

#574 has a narrow formatter/modal wrapping observation, but authenticated full-page
and Edit mounting remain unobserved. #575 has no published/browser observation;
local generated dist bundles were stale and are not current-head evidence.
No full-suite success, release-branch CI, production behavior or independent
release approval is claimed by this preparation.

### Local validation and handoff

Release-helper preparation and offline check passed; helper tests passed 21 cases.
Focused version checks (`dotnet test` with the Versioning namespace filter and
`--no-restore`) compiled successfully: 26 passed, zero failures/skips.
`dotnet run --no-launch-profile --no-build -- version` returned exactly
`Wayfarer 1.9.10`. Whitespace checks passed. A UTF-8 comparison verified prior
released notes unchanged after removing only the redundant empty heading.
Changed-work and complete branch-scope Code Guard returned REVIEW only for the
867-line changelog; accepted because chronological release history remains one
coherent navigable document. All other guards pass; no FAIL or INCOMPLETE.

The release-only diff is limited to `Version.props`, `CHANGELOG.md`, this document,
and the compiled-version assertion in `AppVersionProviderTests.cs`.
Prior released changelog content is preserved, placeholders are replaced, and
exactly one empty Unreleased section remains below the new dated release.

Stop at a local checkpoint for independent review against the verified base.
After independent approval, remaining steps are the authorized PR and exact-head
`test` CI gate, normal merge and main synchronization, then separately authorized
tag/release publication, artifact build and checksum recording, production migration,
deployment and runtime verification. No PR, tag, release, deployable artifact or
deployment is created here. Keep #505 and other deferred issues open; Mobile
coordination remains outstanding after backend deployment.
