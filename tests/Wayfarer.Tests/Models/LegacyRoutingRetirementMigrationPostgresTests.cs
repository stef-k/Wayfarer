using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Services.LocationProviders;
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
            var transportProfile = new TransportProfile
            {
                Id = Guid.NewGuid(), Key = $"migration-{Guid.NewGuid():N}", Label = "Migration planning",
                Category = "test", PlanningSpeedKmh = 42, IsActive = true
            };
            var protection = new EphemeralDataProtectionProvider();
            var credentials = new PersonalProviderCredentialService(protection);
            var personalProfile = PersonalLocationProviderProfile.Create(user.Id, PersonalLocationProvider.Geoapify);
            credentials.Replace(personalProfile, "preserved-personal-secret");
            personalProfile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
            personalProfile.SetAuthorization(PersonalProviderCapability.Routing, true);
            credentials.RecordVerification(personalProfile, PersonalProviderCapability.Geocoding,
                PersonalProviderVerification.Verified);
            credentials.RecordVerification(personalProfile, PersonalProviderCapability.Routing,
                PersonalProviderVerification.Verified);
            var selection = PersonalLocationProviderSelection.Create(user.Id);
            selection.Select(PersonalProviderCapability.Geocoding, PersonalLocationProvider.Geoapify);
            selection.Select(PersonalProviderCapability.Routing, PersonalLocationProvider.Geoapify);
            context.AddRange(transportProfile, personalProfile, selection);
            await context.SaveChangesAsync();
            var expectedCredentialGeneration = personalProfile.CredentialGeneration;
            var expectedGeocodingGeneration = personalProfile.GeocodingGeneration;
            var expectedRoutingGeneration = personalProfile.RoutingGeneration;
            var expectedGeocodingSelectionGeneration = selection.GeocodingSelectionGeneration;
            var expectedRoutingSelectionGeneration = selection.RoutingSelectionGeneration;
            await SeedLegacyRowsAsync(context, user.Id, providerId, transportProfile.Id, tripId, segmentId);

            await migrator.MigrateAsync();
            context.ChangeTracker.Clear();

            await AssertPreservedDataAsync(context, user.Id, providerId, segmentId, transportProfile,
                personalProfile.Id, credentials, expectedCredentialGeneration, expectedGeocodingGeneration,
                expectedRoutingGeneration, expectedGeocodingSelectionGeneration, expectedRoutingSelectionGeneration);
            await AssertLegacyAuthorityRemovedAsync(context);
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

    private static async Task AssertPreservedDataAsync(
        ApplicationDbContext context, string userId, Guid providerId, Guid segmentId,
        TransportProfile transportProfile, Guid personalProfileId, PersonalProviderCredentialService credentials,
        int credentialGeneration, int geocodingGeneration, int routingGeneration,
        int geocodingSelectionGeneration, int routingSelectionGeneration)
    {
        var segment = await context.Segments.AsNoTracking().SingleAsync(item => item.Id == segmentId);
        Assert.Equal((providerId, 7, "geoapify", "drive", transportProfile.Id),
            (segment.RouteProviderConfigurationId, segment.RouteProviderConfigurationVersion,
                segment.RouteProvider, segment.RouteMappingMode, segment.RouteTransportProfileId));
        Assert.Equal("LINESTRING (1 1, 1.5 1.5, 2 2)", segment.RouteGeometry!.AsText());
        Assert.Equal((12.5, TimeSpan.FromMinutes(17), "Preserved attribution", "persistent"),
            (segment.EstimatedDistanceKm, segment.EstimatedDuration, segment.RouteAttribution, segment.RouteStorageMode));
        Assert.Equal(DateTimeOffset.Parse("2026-09-01T12:30:00Z"), segment.RouteGeneratedAt);
        Assert.Equal([new RouteInstruction("Continue", "straight", 0, 2, 12500, 1020)],
            JsonSerializer.Deserialize<RouteInstruction[]>(segment.RouteInstructionsJson!)!);
        var retainedTransportProfile = await context.Set<TransportProfile>().AsNoTracking()
            .SingleAsync(item => item.Id == transportProfile.Id);
        Assert.Equal((transportProfile.Key, "Migration planning", "test", 42d, true),
            (retainedTransportProfile.Key, retainedTransportProfile.Label, retainedTransportProfile.Category,
                retainedTransportProfile.PlanningSpeedKmh, retainedTransportProfile.IsActive));
        var retainedPersonalProfile = await context.PersonalLocationProviderProfiles.AsNoTracking()
            .SingleAsync(item => item.Id == personalProfileId);
        var retainedSelection = await context.PersonalLocationProviderSelections.AsNoTracking()
            .SingleAsync(item => item.UserId == userId);
        Assert.Equal((credentialGeneration, geocodingGeneration, routingGeneration),
            (retainedPersonalProfile.CredentialGeneration, retainedPersonalProfile.GeocodingGeneration,
                retainedPersonalProfile.RoutingGeneration));
        Assert.Equal(("geoapify", "geoapify", geocodingSelectionGeneration, routingSelectionGeneration),
            (retainedSelection.GeocodingProviderKey, retainedSelection.RoutingProviderKey,
                retainedSelection.GeocodingSelectionGeneration, retainedSelection.RoutingSelectionGeneration));
        Assert.Equal("preserved-personal-secret", credentials.Read(retainedPersonalProfile).Credential);
        Assert.NotNull((await new AuthoritativeRoutingProviderResolver(context, credentials)
            .ResolveNativeAsync(userId, "drive", CancellationToken.None)).Execution);
    }

    private static async Task AssertLegacyAuthorityRemovedAsync(ApplicationDbContext context)
    {
        Assert.False(await RelationExistsAsync(context, "UserRoutingConfigurations"));
        Assert.False(await RelationExistsAsync(context, "RoutingProviderProfileMappings"));
        Assert.False(await RelationExistsAsync(context, "RoutingProviderConfigurations"));
        Assert.False(await ColumnExistsAsync(context, "ApplicationSettings", "ExternalRouteGenerationEnabled"));
        Assert.False(await ColumnExistsAsync(context, "ApplicationSettings", "ExternalRouteGenerationVersion"));
        Assert.False(await ColumnExistsAsync(context, "ApplicationSettings", "ActiveRoutingProviderConfigurationId"));
        Assert.False(await RoutineExistsAsync(context, "CreateDefaultUserRoutingConfiguration"));
        Assert.False(await TriggerExistsAsync(context, "TR_AspNetUsers_CreateDefaultRoutingConfiguration"));
        Assert.False(await SegmentProvenanceForeignKeyExistsAsync(context));
        Assert.False(await ColumnNamedAsync(context, "CredentialCiphertext"));
    }

    private static async Task SeedLegacyRowsAsync(ApplicationDbContext context, string userId,
        Guid providerId, Guid transportProfileId, Guid tripId, Guid segmentId)
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
            INSERT INTO "RoutingProviderProfileMappings"
                ("RoutingProviderConfigurationId", "TransportProfileId", "OsrmProfile")
            VALUES ({{providerId}}, {{transportProfileId}}, 'driving');
            INSERT INTO "Trips" ("Id", "UserId", "Name", "IsPublic", "ShareProgressEnabled", "UpdatedAt")
            VALUES ({{tripId}}, {{userId}}, 'retirement fixture', FALSE, FALSE, CURRENT_TIMESTAMP);
            INSERT INTO "Segments" ("Id", "UserId", "TripId", "Mode", "TransportProfileId",
                "EstimatedDistanceKm", "EstimatedDuration", "EstimatedDurationSource", "DisplayOrder",
                "RouteGeometry", "RouteProvider", "RouteProviderConfigurationId", "RouteProviderConfigurationVersion",
                "RouteTransportProfileId", "RouteMappingMode", "RouteInstructionsJson", "RouteGeneratedAt",
                "RouteAttribution", "RouteStorageMode")
            VALUES ({{segmentId}}, {{userId}}, {{tripId}}, 'manual-planning', {{transportProfileId}},
                12.5, INTERVAL '17 minutes', 0, 1,
                ST_GeomFromText('LINESTRING(1 1,1.5 1.5,2 2)',4326), 'geoapify', {{providerId}}, 7,
                {{transportProfileId}}, 'drive',
                '[{"Text":"Continue","Type":"straight","FromIndex":0,"ToIndex":2,"DistanceMetres":12500,"DurationSeconds":1020}]',
                TIMESTAMPTZ '2026-09-01 12:30:00+00', 'Preserved attribution', 'persistent');
            """);
    }

    private static Task<bool> RelationExistsAsync(ApplicationDbContext context, string name) =>
        ScalarAsync(context, "SELECT to_regclass('public.\"' || @name || '\"') IS NOT NULL", name);

    private static Task<bool> ColumnExistsAsync(ApplicationDbContext context, string table, string column) =>
        ScalarAsync(context, "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name=@name AND column_name=@extra)", table, column);

    private static Task<bool> RoutineExistsAsync(ApplicationDbContext context, string name) =>
        ScalarAsync(context, "SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname=@name)", name);

    private static Task<bool> TriggerExistsAsync(ApplicationDbContext context, string name) =>
        ScalarAsync(context, "SELECT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname=@name AND NOT tgisinternal)", name);

    private static Task<bool> ColumnNamedAsync(ApplicationDbContext context, string name) =>
        ScalarAsync(context, "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND column_name=@name)", name);

    private static async Task<bool> SegmentProvenanceForeignKeyExistsAsync(ApplicationDbContext context)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu ON tc.constraint_name = kcu.constraint_name
                    AND tc.constraint_schema = kcu.constraint_schema
                WHERE tc.table_schema = 'public' AND tc.table_name = 'Segments'
                    AND tc.constraint_type = 'FOREIGN KEY'
                    AND kcu.column_name IN ('RouteProviderConfigurationId', 'RouteProviderConfigurationVersion'))
            """;
        if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync();
        return (bool)(await command.ExecuteScalarAsync())!;
    }

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
