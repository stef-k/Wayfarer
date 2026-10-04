# Developers & Contributors

Start here to develop Wayfarer's web application or integrate with its APIs.
The backend uses ASP.NET Core MVC and PostgreSQL/PostGIS; the frontend combines Razor
and JavaScript with a Vue/Vite Trip Editor. The mobile companion lives in the separate
[WayfarerMobile repository](https://github.com/stef-k/WayfarerMobile).

## Start locally

Follow [Setup](14-Setup.md) for the .NET SDK, PostgreSQL/PostGIS, Node/npm, local
configuration, database preparation and frontend development commands. Ubuntu on
Linux/WSL2 is the primary development baseline. Use throwaway Development credentials
and keep secrets out of Git.

## Find the implementation

- [Architecture](15-Architecture.md): application areas, data flow and frontend/backend boundaries. Backend entry and UI live in `Program.cs`, `Areas/` and `Views/`; services, jobs and parsers have their own directories.
- [Frontend architecture](15-Architecture.md#frontend-architecture): area-aligned JavaScript under `wwwroot/js` and the Vue Trip Editor under `ClientApps/trip-editor/src`.
- [Configuration](16-Configuration.md): settings, storage and environment-specific configuration.
- [Services](17-Services.md): shared application behavior, parsers and background jobs.
- [API & Mobile Integration](18-API.md): authentication, endpoint contracts and live-update streams.
- [Database](19-Database.md): EF Core models, migrations and PostGIS spatial data.
- [Security](21-Security.md) and [Personal Location Providers](24-Personal-Location-Providers.md): request protections, privacy and provider integration boundaries.

## Test changes

Follow [Testing](22-Testing.md) for commands, prerequisites and CI scope. Start with
focused tests at the lowest stable seam: unit/client tests for algorithms and state,
PostgreSQL tests for persistence and locking, and browser checks when the behavior
requires a mounted browser. Run Code Guard over the changed scope and report any
unavailable validation clearly.

## Contribute

Read the [repository guidance](https://github.com/stef-k/Wayfarer/blob/main/AGENTS.md)
before changing code. Keep changes small and focused, follow existing conventions,
and document the code and behavior you touch. Add proportionate regression coverage
for behavior changes and update the relevant documentation.

In a pull request, explain the problem and resulting behavior, link the issue where
applicable, and report validation and any remaining gaps. Include screenshots for UI
changes and migration notes when database changes require them.

## Project Maintainers

Release/version/publication ownership, lifecycle qualification, container/Compose
internals, exceptional recovery authority and platform qualification belong in the
[Project Maintainers documentation](./#project-maintainers).
