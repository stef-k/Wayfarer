using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Models;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves settings mutations are scoped solely to the authenticated user identity.</summary>
public sealed class UserRoutingAuthorizationTests : TestBase
{
    [Fact]
    public async Task ForeignUserMutationReturnsMissingWithoutChangingOwnerState()
    {
        const string ownerId = "owner";
        var db = CreateDbContext();
        var owner = UserRoutingConfiguration.CreateServerDefault(ownerId);
        db.Set<UserRoutingConfiguration>().Add(owner);
        db.ApplicationSettings.Add(new ApplicationSettings { Id = 1, ExternalRouteGenerationEnabled = true });
        db.SaveChanges();
        var service = new UserRoutingConfigurationService(db,
            new UserRoutingCredentialService(new EphemeralDataProtectionProvider()));

        var result = await service.SaveAsync(
            "foreign-user", Guid.NewGuid(), "never-persisted", 0, CancellationToken.None);

        Assert.True(result.Missing);
        Assert.Null(owner.SelectedProviderConfigurationId);
        Assert.False(owner.CredentialPresent);
        Assert.DoesNotContain(db.AuditLogs, item => item.UserId == "foreign-user");
    }
}
