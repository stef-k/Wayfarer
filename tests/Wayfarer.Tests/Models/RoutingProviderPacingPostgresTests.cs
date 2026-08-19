using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Proves the provider pacing migration contract on guarded PostgreSQL.</summary>
[Collection(PostgresEnvironmentEvidenceTestCollection.Name)]
public sealed class RoutingProviderPacingPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Proves database default, inclusive constraint, and unchanged xmin authority.</summary>
    [PostgresFact]
    public async Task MinimumInterval_DefaultConstraintAndXmin_AreAuthoritative()
    {
        fixture.RequireAvailable();
        await using var context = fixture.CreateContext();
        var providerId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "RoutingProviderConfigurations"
                ("Id", "DisplayName", "AdapterType", "CredentialPresent", "CredentialRequired", "Enabled",
                 "ConfigurationVersion", "GenerationTimeoutSeconds", "ResponseSizeLimitBytes", "RequestsPerMinute", "MaxConcurrency")
            VALUES ({{providerId}}, {'P' + "acing fixture"}, 1, false, false, false, 1, 15, 1048576, 60, 4)
            """);
        Assert.Equal(1000, await context.Set<RoutingProviderConfiguration>().AsNoTracking()
            .Where(item => item.Id == providerId).Select(item => item.MinimumIntervalMilliseconds).SingleAsync());

        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        var firstCopy = await first.Set<RoutingProviderConfiguration>().SingleAsync(item => item.Id == providerId);
        var staleCopy = await second.Set<RoutingProviderConfiguration>().SingleAsync(item => item.Id == providerId);
        firstCopy.MinimumIntervalMilliseconds = 60000;
        await first.SaveChangesAsync();
        staleCopy.MinimumIntervalMilliseconds = 0;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "RoutingProviderConfigurations" SET "MinimumIntervalMilliseconds" = {60001} WHERE "Id" = {providerId}"""));
        await context.Set<RoutingProviderConfiguration>().Where(item => item.Id == providerId).ExecuteDeleteAsync();
    }
}
