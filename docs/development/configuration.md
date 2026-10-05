---
title: Development configuration
---

# Development configuration

Wayfarer uses ASP.NET Core configuration for process/static runtime settings and a separate database-backed `ApplicationSettings` row for administrator-editable behavior. Keep those authorities distinct when adding a setting.

## Configuration sources and precedence

`WebApplication.CreateBuilder` supplies the normal ASP.NET Core configuration pipeline. In Development this includes `appsettings.json`, `appsettings.Development.json`, .NET user-secrets, environment variables and command-line configuration.

`ApplicationConfiguration.Configure` then appends the base JSON file, the current environment JSON file and environment variables again, with environment variables last. ASP.NET Core uses the last provider containing a key. Consequently, environment variables are the reliable final static override for keys also present in checked-in JSON. User-secrets remain part of the Development provider set, but a key repeated by Wayfarer's later JSON providers can be superseded by that later JSON.

Use double underscores for nested environment-variable keys, for example:

~~~sh
export ConnectionStrings__DefaultConnection='Host=localhost;Port=5432;Database=wayfarer;Username=postgres;Password=local-secret'
~~~

Never commit real credentials to either appsettings file.

## Database connection

`ConnectionStrings:DefaultConnection` is the EF and Quartz PostgreSQL connection authority. The Development JSON points at local PostgreSQL with a placeholder password. Npgsql/NetTopologySuite uses this connection for the application model, while Quartz uses the same database through its separate `qrtz_` schema/table family.

See [Database](database.md) before changing migrations or persistence behavior.

## Storage roots

The `Storage` section binds to `StorageOptions` and is resolved once into `StoragePaths`:

- `Storage:DataRoot` is durable state outside PostgreSQL, including uploads and the default Data Protection directory.
- `Storage:CacheRoot` is rebuildable cache state such as tiles, images and thumbnails.
- `Storage:LogRoot` is the operational file-log directory.
- `Storage:TempRoot` is ephemeral work.

In Development, an omitted root resolves to a per-user platform/XDG default. Outside Development, every root must be configured; an explicitly empty value is invalid. Consumers should depend on `StoragePaths` or the owning storage service rather than recomputing these locations.

`CacheSettings:TileCacheDirectory` and `CacheSettings:ImageCacheDirectory` remain compatibility inputs for legacy cache locations. New writes use the `StoragePaths` cache roots.

## Static/runtime settings versus ApplicationSettings

Static/runtime configuration is appropriate for process identity, connection and host/network/runtime wiring: values that must exist before or while the host is being composed.

`ApplicationSettings` is a database row loaded through `IApplicationSettingsService` and cached in memory. It owns administrator-editable application policy such as registration, location thresholds, upload limits, tile/cache behavior, proxy-image limits and visit-detection settings. Changing it is a persistence/UI concern, not an appsettings-only change.

When adding a setting, choose one authority. Do not create two competing sources with undocumented fallback behavior.

## Host and network concepts

`AllowedHosts` is ASP.NET Core host filtering and is also used when Wayfarer decides whether it has a trustworthy public host identity for upstream tile requests. Development may use the checked-in wildcard; deployed instances should follow [self-hosting configuration](../self-hosting/index.md).

`Application:ContactEmail` supplies an honest contact identity for upstream tile traffic. Do not reuse it as a credential store.

`TrustedProxy:Addresses` and `TrustedProxy:Networks` opt into forwarded-header trust. With neither configured, forwarded headers are disabled. Configured proxies/networks are explicit, a `/0` trust network is rejected, and forwarding is limited to one hop. Security-sensitive code should use the effective request/client identity after this authority rather than parsing forwarding headers independently.

## SSE configuration

`Sse` binds to `SseOptions`, which validates the generic transport/admission limits used by `SseAdmission` and `SseService`. `MobileSse:HeartbeatIntervalMilliseconds` controls protected group/mobile heartbeat timing; Development uses a shorter checked-in heartbeat than the base configuration.

Treat these as transport limits. Resource authorization remains in the endpoint/service owning the stream.

## Providers and browser runtime

Personal location-provider profiles, selections, protected credentials and usage/admission state are database-owned. Process configuration supplies only runtime/default infrastructure used by those services. Do not place user provider credentials in appsettings or expose them through diagnostics.

Browser/PDF/thumbnail work uses the repository's Playwright/Chromium runtime and existing browser services. Development and tests may select browser caches through the documented Playwright environment variables; production browser packaging belongs to the supported release/self-hosting path.

## Production and Compose configuration

This page describes source/native development configuration. Supported Compose deployment has its own generated/operator configuration contract. Do not translate a Development appsettings example into production instructions; use the [self-hosting guide](../self-hosting/index.md) and [native/manual deployment guide](../self-hosting/native-manual.md) when that boundary is relevant.
