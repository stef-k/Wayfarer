---
title: Developers and contributors
---

# Developers and contributors

This is the starting point for working on the Wayfarer web application. It covers source development, repository orientation and the shortest path from a change to useful validation. Supported installation and production operation are separate concerns; use the [self-hosting guide](../self-hosting/index.md) for those.

## Maintained development baseline

- .NET 10 SDK.
- Node.js 24 and npm. `.nvmrc` selects Node 24.
- PostgreSQL 18 with PostGIS for local development and guarded relational tests. `citext` is also used by the EF model.
- Ubuntu 24.04 on Linux or WSL2 is the primary development baseline. Windows is supported as an alternative development environment.

WayfarerMobile is a separate repository. Changes to the server do not implicitly change the mobile application.

## Restore and run

Clone into a native filesystem for the environment you are using. On WSL2, prefer a path such as `~/src/Wayfarer` rather than `/mnt/c/...`.

Restore the .NET tools, application dependencies and locked frontend dependencies:

~~~sh
dotnet tool restore
dotnet restore
nvm install
nvm use
npm ci
~~~

Create or select a local PostgreSQL database, enable `postgis` and `citext`, and configure `ConnectionStrings:DefaultConnection`. The checked-in Development JSON contains only a placeholder password; do not commit credentials. An environment-variable override is unambiguous:

~~~sh
export ConnectionStrings__DefaultConnection='Host=localhost;Port=5432;Database=wayfarer;Username=postgres;Password=your-local-password'
~~~

Start the ASP.NET Core application in Development:

~~~sh
dotnet run
~~~

`dotnet watch run` is the normal hot-reload alternative. Non-Production startup applies pending EF migrations, aligns the Quartz schema and seeds required development data. Production startup deliberately does not perform those mutations; see [Database](database.md) and the [self-hosting documentation](../self-hosting/index.md).

## Frontend development

Wayfarer has two frontend paths that share the same ASP.NET Core application:

- Razor views use MvcFrontendKit. `frontend.config.yaml` owns global assets and maps `Views/{Controller}/{Action}` or `Areas/{Area}/{Controller}/{Action}` to matching JavaScript under `wwwroot/js/`.
- The Trip Editor is a Vue application under `ClientApps/trip-editor/`. Vite serves it during development and builds it to `wwwroot/vite/trip-editor/` with `manifest.json` as the ASP.NET integration point.

For Razor/MvcFrontendKit production bundles, use the repository-local tool:

~~~sh
dotnet frontend build
~~~

For the Trip Editor, run Vite beside the ASP.NET application when editing the Vue client:

~~~sh
npm run dev
~~~

Build the Trip Editor assets with:

~~~sh
npm run typecheck
npm run build
~~~

Generated `wwwroot/dist/` and `wwwroot/vite/` outputs are build products, not source ownership boundaries.

## Repository orientation

Start from responsibilities rather than individual files:

- `Program.cs` composes startup, middleware, Identity, EF, Quartz and application services.
- `Areas/` contains the Admin, Manager, User, API and Public HTTP surfaces; ASP.NET Core Identity pages live under the Identity area.
- `Controllers/` and area controllers own HTTP translation. Domain and workflow behavior normally belongs in `Services/`, not in views or JavaScript.
- `Models/`, `Models/Configuration/` and EF configuration own durable shapes and relational rules; `Migrations/` records schema evolution.
- `Jobs/` contains Quartz job entry points. Long-running import/enrichment workflow ownership also lives under the corresponding service namespaces.
- `Parsers/` owns location-import format parsing.
- `ClientApps/trip-editor/` owns Vue Trip Editor state and behavior. `wwwroot/js/` owns Razor/area-aligned JavaScript.
- `tests/Wayfarer.Tests/` and `tests/client/` hold the .NET and client test seams.

Read [Architecture](architecture.md) before changing an unfamiliar subsystem. Use [Extending Wayfarer](extending.md) for task-oriented recipes.

## Development and production are different paths

`dotnet run` is a source-development path. It is not the supported way to install or operate a production Wayfarer instance. Production/self-hosting uses the documented release, Compose and `wayfarerctl` flow under [Self-hosting](../self-hosting/index.md). Keep operator credentials, backup/update procedures and lifecycle qualification out of source-development instructions.

## Contribution path

Before editing, read the repository-root [AGENTS.md](../../AGENTS.md). It owns contribution, review, branch, merge and evidence policy; this guide does not duplicate those rules.

A practical development loop is:

1. Locate the owning HTTP, service, persistence and client boundary.
2. Make the smallest coherent change in that owner.
3. Prove behavior at the lowest reliable test seam.
4. Add relational or browser evidence only when the requirement depends on those layers.
5. Run the relevant frontend checks, Code Guard and repository CI described in [Testing](testing.md).
6. Update the canonical documentation when the public developer contract changes.

Continue with [Architecture](architecture.md), [Configuration](configuration.md), [Database](database.md), [API](api.md), [Security](security.md), [Extending Wayfarer](extending.md) and [Testing](testing.md).
