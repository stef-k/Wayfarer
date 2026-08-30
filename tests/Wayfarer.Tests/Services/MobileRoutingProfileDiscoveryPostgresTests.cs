using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves the production discovery projection retains its relational candidate bound.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class MobileRoutingProfileDiscoveryPostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresFact]
    public void EligibleProjectionAppliesSqlLimitBeforeMaterialization()
    {
        fixture.RequireAvailable();
        using var db = fixture.CreateContext();
        var protection = new EphemeralDataProtectionProvider();
        var service = new MobileRoutingProfileDiscoveryService(db, new(protection), new(protection),
            new PersonalProviderCredentialService(protection));

        var sql = service.EligibleQuery(Guid.NewGuid()).ToQueryString();

        Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("101", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
    }
}
