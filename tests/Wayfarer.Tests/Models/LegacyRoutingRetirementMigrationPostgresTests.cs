using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql;
using System.Runtime.ExceptionServices;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Proves legacy routing authority is deleted while accepted Segment provenance survives.</summary>
[Collection(PostgresMigrationTestCollection.Name)]
public sealed class LegacyRoutingRetirementMigrationPostgresTests(PostgresMigrationTestFixture fixture)
{
    private const string PreviousMigration = "20260831161937_CorrectConcurrentCompatibilityProfileCreation";

    [PostgresFact]
    public async Task Migration_RemovesAuthorityInDependencyOrderAndRetainsScalarProvenance()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        await using var context = fixture.CreateContext();
        var migrator = context.GetService<IMigrator>();
        Exception? primary = null;
        try
        {
            await migrator.MigrateAsync(PreviousMigration);
            var providerId = Guid.NewGuid();
            var tripId = Guid.NewGuid();
            var segmentId = Guid.NewGuid();
            await SeedLegacyRowsAsync(context, user.Id, providerId, tripId, segmentId);

            await migrator.MigrateAsync();
            context.ChangeTracker.Clear();

            var segment = await context.Segments.AsNoTracking().SingleAsync(item => item.Id == segmentId);
            Assert.Equal(providerId, segment.RouteProviderConfigurationId);
            Assert.Equal(7, segment.RouteProviderConfigurationVersion);
            Assert.Equal("geoapify", segment.RouteProvider);
            Assert.Equal("drive", segment.RouteMappingMode);
            Assert.False(await RelationExistsAsync(context, "UserRoutingConfigurations"));
            Assert.False(await RelationExistsAsync(context, "RoutingProviderProfileMappings"));
            Assert.False(await RelationExistsAsync(context, "RoutingProviderConfigurations"));
            Assert.False(await ColumnExistsAsync(context, "ApplicationSettings", "ExternalRouteGenerationEnabled"));
            Assert.False(await RoutineExistsAsync(context, "CreateDefaultUserRoutingConfiguration"));
        }
        catch (Exception failure) { primary = failure; }
        finally
        {
            try { await migrator.MigrateAsync(); }
            catch when (primary is not null)
            { primary.Data["PostgresMigrationRestore"] = "Latest migration restoration also failed."; }
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }

    private static async Task SeedLegacyRowsAsync(ApplicationDbContext context, string userId,
        Guid providerId, Guid tripId, Guid segmentId)
    {
        await context.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "RoutingProviderConfigurations"
                ("Id", "DisplayName", "AdapterType", "CredentialPresent", "CredentialRequired",
                 "PersonalRoutingAccess", "Enabled", "ConfigurationVersion", "GenerationTimeoutSeconds",
                 "ResponseSizeLimitBytes", "RequestsPerMinute", "MinimumIntervalMilliseconds", "MaxConcurrency")
            VALUES ({{providerId}}, 'legacy', 2, TRUE, TRUE, 2, TRUE, 7, 30, 2000000, 60, 0, 2);
            UPDATE "RoutingProviderConfigurations" SET "CredentialCiphertext" = 'retire-me' WHERE "Id" = {{providerId}};
            UPDATE "ApplicationSettings" SET "ExternalRouteGenerationEnabled" = TRUE,
                "ActiveRoutingProviderConfigurationId" = {{providerId}} WHERE "Id" = 1;
            INSERT INTO "UserRoutingConfigurations"
                ("UserId", "SelectedProviderConfigurationId", "CredentialCiphertext", "CredentialPresent",
                 "ConfigurationVersion", "CreatedAt", "UpdatedAt")
            VALUES ({{userId}}, {{providerId}}, 'retire-user-secret', TRUE, 1, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            ON CONFLICT ("UserId") DO UPDATE SET "SelectedProviderConfigurationId" = EXCLUDED."SelectedProviderConfigurationId",
                "CredentialCiphertext" = EXCLUDED."CredentialCiphertext", "CredentialPresent" = TRUE;
            INSERT INTO "Trips" ("Id", "UserId", "Name", "IsPublic", "ShareProgressEnabled", "UpdatedAt")
            VALUES ({{tripId}}, {{userId}}, 'retirement fixture', FALSE, FALSE, CURRENT_TIMESTAMP);
            INSERT INTO "Segments" ("Id", "UserId", "TripId", "Mode", "EstimatedDistanceKm", "DisplayOrder",
                "RouteGeometry", "RouteProvider", "RouteProviderConfigurationId", "RouteProviderConfigurationVersion",
                "RouteMappingMode", "RouteStorageMode")
            VALUES ({{segmentId}}, {{userId}}, {{tripId}}, 'car', 1.0, 1,
                ST_GeomFromText('LINESTRING(1 1,2 2)',4326), 'geoapify', {{providerId}}, 7, 'drive', 'persistent');
            """);
    }

    private static Task<bool> RelationExistsAsync(ApplicationDbContext context, string name) =>
        ScalarAsync(context, "SELECT to_regclass('public.\"' || @name || '\"') IS NOT NULL", name);

    private static Task<bool> ColumnExistsAsync(ApplicationDbContext context, string table, string column) =>
        ScalarAsync(context, "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name=@name AND column_name=@extra)", table, column);

    private static Task<bool> RoutineExistsAsync(ApplicationDbContext context, string name) =>
        ScalarAsync(context, "SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname=@name)", name);

    private static async Task<bool> ScalarAsync(ApplicationDbContext context, string sql, string name, string? extra = null)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new NpgsqlParameter("name", name));
        if (extra != null) command.Parameters.Add(new NpgsqlParameter("extra", extra));
        if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync();
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
