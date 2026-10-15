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

## Activity catalog integrity and coordinated deployment

Activity names use the migration-owned
`public.wayfarer_activity_name_v1(raw text, identity boolean)` function on PostgreSQL
18/UTF-8. It trims only surrounding Unicode 16 whitespace, validates XML text,
preserves NFC display spelling/case and computes NFC/full Unicode case-folded
identity with `pg_unicode_fast`. Interior spaces, tabs, NBSP and line breaks remain
significant. The stored generated `NormalizedName` shadow property uses deterministic
`C` equality and a unique index; there is no second .NET name matcher or arbitrary
name-length cap. Real full-key B-tree capacity still limits storage. Qualify these
vectors and reconcile/rebuild deliberately before any PostgreSQL-major upgrade;
never change v1 semantics under existing generated keys.

Migration `20261013152203_ActivityCatalogIntegrity` preflights names, positive IDs,
full-key index capacity and every referencing FK while holding fixed-order catalog,
Location and redirect table locks. It aborts on unknown/weakened references. Lowest
ID wins each equivalent-name group. Only Location Activity FKs change, across all
owners; all other metadata stays untouched. Nonblank description text is compared
exactly: the survivor's prose wins as primary, or the lowest-ID nonblank source if
absent. Other distinct texts append in source-ID order with the original name/ID
heading. Exact repeats append once. Any failure rolls back the catalog, descriptions,
Locations, redirects and schema together; sequence gaps after rollback are harmless.

`ActivityTypeIdRedirects` retains each removed positive ID indefinitely and points
directly to its live survivor. The sequence advances beyond the previous high-water
mark; database guards reject explicit reuse. API check-in/log-location/PUT and owner
MVC Create/Edit resolve live ID first, then a redirect. Renaming preserves IDs and
redirects. Both Location and redirect FKs remain NO ACTION/NOT DEFERRABLE and EF
ClientNoAction, so deletion cannot clear tracked optional references. Admin create
and rename report normalized conflicts, including races; deletion reports references
and remains possible for unreferenced, unmapped entries. Description replacement is
an explicit administrator edit. Bundled data seeds only an empty catalog through v1.

Deploy this schema and compatible API/Admin binary as **one coordinated change**.
Stop/fence all old catalog writers and imports; no rolling overlap with incompatible
binaries is supported. Under the existing [release authority](../maintainer/versioning.md)
and [update/recovery procedure](../self-hosting/wayfarerctl.md#update), retain a verified
quiesced pre-migration backup before preparation. For managed Compose, the existing
update plan requires a fresh verified quiesced recovery set; review target/source
authority through that workflow. Source/native operators must likewise stop every
writer and verify their backup before explicit production preparation. Resume client
traffic only after the compatible resolver binary is active. Production startup
still does not migrate automatically; this PR changes no release versions/assets.

Down is deliberately **lossy**: it removes the generated key/check/index/function,
retains merged descriptions and survivor FKs, and keeps redirects plus retired-ID
guards. It cannot recreate original IDs or FK assignments. A code rollback must
retain compatible redirect resolution; otherwise restore the verified pre-cleanup
backup with writers stopped before running an old binary. Re-Up after Down and
ordinary migration-history reruns are supported. There is no redirect expiry.

This is the A+B prerequisite in [#862](https://github.com/stef-k/Wayfarer/issues/862).
Native export/import fidelity remains separately owned by #814. See the
[accepted design](activity-reconciliation-design.md) for the slice boundaries.

### Activity reconciliation during location imports

The #863 worker uses the concrete `ActivityCatalogReconciler` with its current
context and READ COMMITTED transaction. Existing UUID/keyless filtering and keyed
savepoint arbitration run first, inserting provisional Locations with null Activity
FKs. Only actual new rows are normalized in one bounded query. PostgreSQL orders
keys with `C`; earliest accepted file order supplies a new entry's display. Existing
entries are locked FOR SHARE through commit. A parameterized ON CONFLICT DO NOTHING
insert is followed by a separate winning-row query/lock, avoiding the concurrent
INSERT snapshot visibility gap. Names and descriptions on existing entries stay
unchanged; new entries have null Description. Imported numeric IDs have no authority.

Catalog work uses transaction-local 5-second statement and 2-second lock timeouts,
restoring prior settings before unrelated persistence. A vanished/renamed winner
gets at most two resolution attempts. Lock/statement timeout, exhausted resolution,
deadlock or serialization failure rolls back the entire batch before at most two
retries with fresh contexts and 100/200 ms cancellation-aware backoff. One 30-second
deadline covers attempts/backoff; deadline exhaustion is failure, distinct from user
cancellation. Every attempt resets provisional IDs/FKs, reruns duplicate arbitration
and checks exact execution authority, with a final epoch/xmin check before commit.
An unconfirmed commit is read from a fresh durable checkpoint, never blindly retried.

Locations, catalog creations, duplicate/enrichment facts, invalid count and the
existing 50-row checkpoint commit atomically. Valid reconciliation/storage/schema
failure cannot commit a valid-but-unclassified Location. Failure retains the staged
file and prior committed prefix for ordinary owner restart/resume. Cancellation or
stale authority rolls back uncommitted work. There is no creation quota or account
counter; existing upload/admission and batch/database resource bounds still apply.

Migration `20261013200141_AddImportUnclassifiedActivityCount` adds one nonnegative integer with
default zero; old imports start at zero because historical invalidity cannot be
inferred. Only accepted new rows with invalid supplied values increment it. Missing
or blank names, duplicates, race losers and rolled-back rows do not. A transient
parser flag distinguishes non-string native JSON values without retaining private
labels. A bounded raw-string reader detects lone surrogate escapes before Json.NET
can replace them; valid replacement characters and escaped literal text remain valid.
Strict UTF-8 decoding preserves supported BOM-prefixed native files and rejects
malformed input bytes. SQL failures propagate as batch failures. The owner list renders the durable
Invalid Activity count beside existing errors; no raw names enter logs or SSE.

Native owner-history exports load Activity names in the shared authenticated
query. CSV, Wayfarer GeoJSON, GPX and location-history KML carry the current catalog
name, resolved by the destination worker, plus independent nullable LocationType
text. Only missing/null/empty LocationType becomes null; nonempty whitespace is
significant. XML writers entitize carriage returns and subtree readers retain
whitespace so native export/import/re-export preserves both fields. GPX/KML stay
keyless; CSV/GeoJSON retain stored nullable UUIDs and first-writer semantics.
XML-incompatible LocationType text uses the versioned strict UTF-8/Base64 fallback
defined in [design §7](activity-reconciliation-design.md#7-native-history-fidelity-and-compatibility);
ordinary XML-compatible text keeps its existing representation. Both XML parsers
reject malformed or conflicting fallback fields explicitly.
This fidelity correction (#814) adds no migration.

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
