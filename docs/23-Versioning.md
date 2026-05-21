# Versioning

`Version.props` is the runtime and release version source for this slice. The root
file contains the manually edited `WayfarerVersion` value and maps the standard
MSBuild metadata directly from it:

```xml
<WayfarerVersion>1.4.0</WayfarerVersion>
<Version>$(WayfarerVersion)</Version>
<PackageVersion>$(WayfarerVersion)</PackageVersion>
<AssemblyInformationalVersion>$(WayfarerVersion)</AssemblyInformationalVersion>
<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
```

The running app reads `AssemblyInformationalVersion` from the compiled Wayfarer
assembly through `IAppVersionProvider`. Runtime surfaces such as
`dotnet run -- version`, `GET /api/version`, `X-Wayfarer-Version`, and the shared
layout footer use that provider instead of separate constants.

## Manual bump process

To prepare a later release version, edit only the root `Version.props` file and
change `WayfarerVersion` to the target release value. Rebuild the app so the new
value is compiled into assembly metadata.

Release helper automation, changelog checks, tag validation, and GitHub release
validation are deferred to issue #324.
