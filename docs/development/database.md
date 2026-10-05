---
title: Database development
---

# Database development

Wayfarer's primary store is PostgreSQL accessed through EF Core/Npgsql. Spatial behavior uses PostGIS through NetTopologySuite, and selected case-insensitive text uses PostgreSQL `citext`.

## ApplicationDbContext

`Models/ApplicationDbContext.cs` is a partial class derived from `IdentityDbContext<ApplicationUser>`, so the application model and ASP.NET Core Identity share the same EF context. Cohesive model surfaces can be split into additional `ApplicationDbContext.*.cs` partials, such as the personal-provider state.

`Program.cs` registers a pooled `IDbContextFactory<ApplicationDbContext>` using:

- `ConnectionStrings:DefaultConnection`;
- Npgsql;
- `UseNetTopologySuite()`.

Prefer the existing scoped context or context factory appropriate to the owning workflow. Do not introduce a second persistence abstraction solely to hide EF.

## Model configuration

`OnModelCreating` contains cross-cutting/direct relational rules and then calls `ApplyConfigurationsFromAssembly`, allowing focused `IEntityTypeConfiguration<T>` classes under the model configuration area. Follow the existing owner for the entity you are changing rather than moving unrelated configuration.

Important PostgreSQL conventions include:

- `citext` is declared as a model extension; `Tag.Name` uses `citext` and database uniqueness.
- location and trip spatial columns use WGS84/SRID 4326, including `geography(Point, 4326)`/`geography(Point,4326)` and `geography(LineString,4326)`.
- spatial query owners use GiST indexes where required, for example the main location coordinate index.
- PostgreSQL system column `xmin` is used as the EF row-version/concurrency token for cache metadata where currently configured.
- relationships and delete behavior are explicit where aggregate ownership matters.

Spatial values should keep longitude in X and latitude in Y and set SRID 4326 before persistence.

## Migrations and design-time context

Schema migrations live under `Migrations/`. The repository pins `dotnet-ef` in `.config/dotnet-tools.json`; restore it with:

~~~sh
dotnet tool restore
~~~

`Design/ApplicationDbContextFactory.cs` is the design-time context factory. It reads `DOTNET_ENVIRONMENT`/`ASPNETCORE_ENVIRONMENT` (defaulting to Production), loads the matching appsettings files plus environment variables, and configures Npgsql/NetTopologySuite.

When generating a migration against Development configuration, make the environment explicit and ensure `DefaultConnection` points only at your development database:

~~~sh
DOTNET_ENVIRONMENT=Development dotnet ef migrations add DescriptiveMigrationName
~~~

Inspect both the generated migration and `ApplicationDbContextModelSnapshot.cs`. A migration is part of the durable contract: verify column types, nullability/defaults, indexes, constraints, foreign keys and destructive behavior rather than accepting generated output blindly.

For local application startup, normal non-Production activation calls `Database.MigrateAsync()`, aligns the Quartz schema and seeds development data. `dotnet ef database update` is therefore optional for the ordinary run loop, but can be useful when inspecting a migration deliberately.

## Production migration boundary

Production startup does not mutate schema or bootstrap administrator state. It checks readiness and refuses activation when preparation is incomplete. Supported production migration/seed/bootstrap operations are explicit maintenance actions; follow the [self-hosting guide](../self-hosting/index.md) rather than copying Development startup behavior into deployment code.

## Quartz is a separate schema responsibility

Quartz persists jobs/triggers through its ADO job store using the same PostgreSQL connection and `qrtz_` table prefix. `QuartzSchemaInstaller` owns creation/alignment of that schema. Those tables are not EF entities and should not be modeled as application migrations merely because they live in the same database.

## Adding or changing durable state

For a field/entity/relationship change:

1. Identify the aggregate/service that owns writes and the queries that own reads.
2. Change the model and the existing EF configuration owner.
3. Add indexes/constraints that enforce the real invariant, including spatial or uniqueness requirements.
4. Generate one focused migration and inspect its SQL intent.
5. Update DTO/API/UI mapping only where the field crosses those boundaries.
6. Add the lowest useful behavioral test. Use the dedicated PostgreSQL fixture when provider behavior, constraints, migrations, PostGIS, transactions or concurrency are part of the requirement.

Do not use the normal `wayfarer` development database or any production database for guarded relational fixtures. See [Testing](testing.md#postgresql-and-postgis-tests).

## Transactions and concurrency

Let the service/workflow that owns an invariant own its transaction. Avoid splitting a required atomic transition across controller saves or background-job callbacks. Preserve cancellation tokens on database operations.

When concurrent writers are possible, use an existing database-enforced invariant or concurrency token rather than relying on an in-memory check. Unique constraints, row-version/`xmin` checks and workflow epochs already appear in the repository; choose the concrete mechanism required by the current owner.
