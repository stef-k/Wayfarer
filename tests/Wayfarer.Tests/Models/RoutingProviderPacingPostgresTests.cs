using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Wayfarer.Areas.Admin.Models;
using Wayfarer.Models;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Proves the complete provider pacing persistence contract on guarded PostgreSQL.</summary>
[Collection(PostgresMigrationTestCollection.Name)]
public sealed class RoutingProviderPacingPostgresTests(PostgresMigrationTestFixture fixture)
{
    private const string PreviousMigration = "20260818161609_ExternalRoutingCredentialRequirement";

    /// <summary>Proves migration, boundaries, version invalidation, no-op preservation, xmin, and recovery.</summary>
    [PostgresFact]
    public async Task MinimumInterval_MigrationBoundariesAdministrationAndXmin_AreAuthoritative()
    {
        fixture.RequireAvailable();
        var legacyId = Guid.NewGuid();
        var defaultId = Guid.NewGuid();
        await using var context = fixture.CreateContext();
        var migrator = context.GetService<IMigrator>();
        try
        {
            await migrator.MigrateAsync(PreviousMigration);
            await InsertProviderAsync(context, legacyId);
            await migrator.MigrateAsync();
            Assert.Equal(1000, await IntervalAsync(context, legacyId));

            await InsertProviderAsync(context, defaultId);
            Assert.Equal(1000, await IntervalAsync(context, defaultId));
            await SetIntervalAsync(context, defaultId, 0);
            Assert.Equal(0, await IntervalAsync(context, defaultId));
            await SetIntervalAsync(context, defaultId, 60000);
            Assert.Equal(60000, await IntervalAsync(context, defaultId));
            await Assert.ThrowsAsync<PostgresException>(() => SetIntervalAsync(context, defaultId, -1));
            await SetIntervalAsync(context, defaultId, 0);
            await Assert.ThrowsAsync<PostgresException>(() => SetIntervalAsync(context, defaultId, 60001));
            await SetIntervalAsync(context, defaultId, 1000);

            var provider = await context.Set<RoutingProviderConfiguration>().SingleAsync(item => item.Id == defaultId);
            provider.VerifiedConfigurationVersion = provider.ConfigurationVersion;
            provider.VerificationStatus = "verified";
            await context.SaveChangesAsync();
            var service = new RoutingProviderAdministrationService(context,
                new RoutingProviderCredentialService(new EphemeralDataProtectionProvider()),
                new RoutingProviderPacer(TimeProvider.System));
            var originalVersion = provider.ConfigurationVersion;
            Assert.True((await service.SaveAsync(Model(provider, "1.1"), "admin", CancellationToken.None)).Succeeded);
            Assert.Equal(originalVersion + 1, provider.ConfigurationVersion);
            Assert.Null(provider.VerifiedConfigurationVersion);

            provider.VerifiedConfigurationVersion = provider.ConfigurationVersion;
            provider.VerificationStatus = "verified";
            await context.SaveChangesAsync();
            var noOpVersion = provider.ConfigurationVersion;
            var noOpVerification = provider.VerifiedConfigurationVersion;
            Assert.True((await service.SaveAsync(Model(provider, "1.1"), "admin", CancellationToken.None)).Succeeded);
            Assert.Equal(noOpVersion, provider.ConfigurationVersion);
            Assert.Equal(noOpVerification, provider.VerifiedConfigurationVersion);

            await using var first = fixture.CreateContext();
            await using var stale = fixture.CreateContext();
            var firstCopy = await first.Set<RoutingProviderConfiguration>().SingleAsync(item => item.Id == defaultId);
            var staleCopy = await stale.Set<RoutingProviderConfiguration>().SingleAsync(item => item.Id == defaultId);
            firstCopy.DisplayName = "Pacing fixture current";
            await first.SaveChangesAsync();
            staleCopy.DisplayName = "Pacing fixture stale";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
            stale.ChangeTracker.Clear();
            Assert.Equal("Pacing fixture current", (await stale.Set<RoutingProviderConfiguration>()
                .AsNoTracking().SingleAsync(item => item.Id == defaultId)).DisplayName);
        }
        finally
        {
            await migrator.MigrateAsync();
            await context.Set<RoutingProviderConfiguration>()
                .Where(item => item.Id == legacyId || item.Id == defaultId).ExecuteDeleteAsync();
        }
    }

    private static Task<int> IntervalAsync(ApplicationDbContext context, Guid id) => context
        .Set<RoutingProviderConfiguration>().AsNoTracking().Where(item => item.Id == id)
        .Select(item => item.MinimumIntervalMilliseconds).SingleAsync();

    private static Task<int> SetIntervalAsync(ApplicationDbContext context, Guid id, int value) =>
        context.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "RoutingProviderConfigurations" SET "MinimumIntervalMilliseconds" = {value} WHERE "Id" = {id}""");

    private static Task<int> InsertProviderAsync(ApplicationDbContext context, Guid id) =>
        context.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "RoutingProviderConfigurations"
                ("Id", "DisplayName", "AdapterType", "CredentialPresent", "CredentialRequired", "Enabled",
                 "ConfigurationVersion", "GenerationTimeoutSeconds", "ResponseSizeLimitBytes", "RequestsPerMinute", "MaxConcurrency")
            VALUES ({{id}}, {{"Pacing fixture"}}, 1, false, false, false, 1, 15, 1048576, 60, 4)
            """);

    private static RoutingProviderEditViewModel Model(RoutingProviderConfiguration provider, string interval) => new()
    {
        Id = provider.Id,
        DisplayName = provider.DisplayName,
        BaseEndpoint = provider.BaseEndpoint ?? "https://routing.example",
        Enabled = provider.Enabled,
        CredentialRequired = provider.CredentialRequired,
        CredentialPresent = provider.CredentialPresent,
        ExternalCoordinateDisclosure = provider.ExternalCoordinateDisclosure ?? "Coordinates are sent externally.",
        VerificationFromLongitude = provider.VerificationFromLongitude,
        VerificationFromLatitude = provider.VerificationFromLatitude,
        VerificationToLongitude = provider.VerificationToLongitude,
        VerificationToLatitude = provider.VerificationToLatitude,
        GenerationTimeoutSeconds = provider.GenerationTimeoutSeconds,
        ResponseSizeLimitBytes = provider.ResponseSizeLimitBytes,
        RequestsPerMinute = provider.RequestsPerMinute,
        MaxConcurrency = provider.MaxConcurrency,
        MinimumIntervalSeconds = interval,
        RowVersion = provider.RowVersion,
        ConfigurationVersion = provider.ConfigurationVersion,
        Mappings = []
    };
}
