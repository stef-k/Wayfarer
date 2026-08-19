using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Areas.User.RoutingModels;
using Wayfarer.Models;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Proves the current-user settings projection contains only masked credential state.</summary>
public sealed class RoutingSettingsControllerTests : TestBase
{
    [Fact]
    public async Task IndexProjectsCredentialPresenceWithoutSecretMaterial()
    {
        const string userId = "owner";
        var db = CreateDbContext();
        var profile = db.Set<TransportProfile>().First();
        var protection = new EphemeralDataProtectionProvider();
        var credentials = new UserRoutingCredentialService(protection);
        var provider = new RoutingProviderConfiguration
        {
            Id = Guid.NewGuid(), DisplayName = "Approved", BaseEndpoint = "https://routing.example",
            Enabled = true, PersonalRoutingAccess = PersonalRoutingAccess.CredentialRequired,
            ConfigurationVersion = 2, VerifiedConfigurationVersion = 2,
            Attribution = "Attribution", ExternalCoordinateDisclosure = "Coordinates leave Wayfarer."
        };
        provider.ProfileMappings.Add(new RoutingProviderProfileMapping
        {
            RoutingProviderConfigurationId = provider.Id, TransportProfileId = profile.Id,
            TransportProfile = profile, OsrmProfile = "driving"
        });
        var configuration = UserRoutingConfiguration.CreateServerDefault(userId);
        configuration.SelectPersonalProvider(provider.Id);
        credentials.Replace(configuration, provider.Id, "must-not-render");
        db.Set<RoutingProviderConfiguration>().Add(provider);
        db.Set<UserRoutingConfiguration>().Add(configuration);
        db.SaveChanges();
        var controller = new RoutingSettingsController(db, null!, null!, credentials)
        {
            ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(userId) }
        };

        var view = Assert.IsType<ViewResult>(await controller.Index(CancellationToken.None));
        var model = Assert.IsType<RoutingSettingsViewModel>(view.Model);

        Assert.True(model.CredentialPresent);
        Assert.Null(model.Credential);
        Assert.Equal("Ready", model.Status);
        Assert.DoesNotContain(typeof(RoutingSettingsViewModel).GetProperties(),
            property => property.Name.Contains("Ciphertext", StringComparison.Ordinal));
    }
}
