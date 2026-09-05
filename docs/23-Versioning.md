# Versioning

`Version.props` is the runtime and release version source for this slice. The root
file contains the manually edited `WayfarerVersion` value and maps the standard
MSBuild metadata directly from it:

```xml
<WayfarerVersion>1.9.9</WayfarerVersion>
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
output. The app writes exactly `Wayfarer 1.9.9`; `--no-launch-profile` avoids
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

## 1.9.9 local release review record

Prepared on 2026-09-05 on `feature/release-1.9.9` for independent review.
The clean starting HEAD, local `main`, and fetched `origin/main` all resolved to
`6ebda616975b5c3a5f759fdb566116291f0b8e9f`. The previous release `v1.9.8`
resolves to `4ec2ee5a887753e42198388e806094c117e0b18e`.
The audited first-parent range is `v1.9.8..6ebda616975b5c3a5f759fdb566116291f0b8e9f`:

| PR / issue | First-parent commit | Scope |
| --- | --- | --- |
| [#567](https://github.com/stef-k/Wayfarer/pull/567) / #565 | `cbe16ae6b28376d9e30fce0736f94990463c42af` | Address edit authority and native disclosure marker |
| [#569](https://github.com/stef-k/Wayfarer/pull/569) / #566 | `f9c5e70d3af1aa2fceaf9eda0c8be73bb19506d7` | Cancelled repair commands, progress, and durable retries |
| [#570](https://github.com/stef-k/Wayfarer/pull/570) / #568 | `6ebda616975b5c3a5f759fdb566116291f0b8e9f` | Quartz test isolation, restart proof, and cleanup |

### Migration boundary

No database migration was added in this range. Both endpoint `Migrations` trees
are `690d44c6249db40da98ec6a2f81bed939a8984aa`; the path diff and path history
are empty, including the model snapshot. The latest migration remains
`20260904100416_RetireLegacyRoutingAuthority`. Existing upgrade and rollback
requirements still apply; retain PostgreSQL and its matching Data Protection keys.

### Validation and reused evidence

Local release validation:

- `python tools/release/version.py prepare 1.9.9` completed; placeholder notes replaced.
- `python tools/release/version.py check` passed.
- `python -m pytest tools/release/tests -q`: 21 passed.
- `dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj --no-restore --filter FullyQualifiedName~Wayfarer.Tests.Versioning --verbosity quiet`: 26 passed, zero failures/skips, with compilation.
- `dotnet run --no-launch-profile --no-build -- version`: exactly `Wayfarer 1.9.9`.
- `git diff --check` passed. Released 1.9.8 notes are preserved and one empty Unreleased section remains.
- Changed-work and complete branch-scope Code Guard: accepted REVIEW for the coherent 854-line chronological changelog; no FAIL or INCOMPLETE. All other guards pass. Keeping release history together preserves navigation and responsibility.

Live GitHub checks were read through `gh`: the `test` job succeeded on each
included PR head: #567 `7c3308a15dcbc1cc68ddfd5a0f94ca7c10b09049`,
#569 `871640e8f239035804fe124ad071b6d22af7eef6`, and
#570 `23e42f5d4a263459df163bcb8acb60143401e71e`.
The PR review records report 13 focused tests for #567, 289 for #569 with the
then-excluded restart test, and 290 for #570 with that exclusion removed.
#570 also records the restart proof and 33 scheduler/lifecycle tests. These
selections overlap and are not additive; product tests were not rerun for this
metadata preparation. #568 is closed and its specific restart evidence debt is
restored by #570. No full-suite-green claim is made.

### Evidence limits and review handoff

Browser interaction and screenshots remain unobserved. The continuation proof
accelerates stored deadlines and does not establish five minutes of unattended
waiting. The restart proof mocks batch completion behind the real job; its
no-replay observation covers two seconds plus persisted trigger absence. The
original production inactivity has not been conclusively diagnosed.

Independently review the final local HEAD against the source base above: confirm
the four-file release-only diff (including the pinned compiled-version assertion),
changelog preservation, migration boundary, and validation evidence. This record
is preparation evidence; no release-branch CI or independent approval is claimed.
No PR, tag, GitHub release, deployable artifact, or deployment is created here;
artifact identities and checksums remain unavailable until artifacts exist.
Mobile and tracker issues are unchanged. Keep #505 open for coordination after
backend deployment. Stop at the local checkpoint for independent review.
