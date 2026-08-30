using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Exercises bounded local-only Mobile profile discovery behavior.</summary>
public sealed class MobileRoutingProfileDiscoveryTests : TestBase
{
    [Fact]
    public async Task AvailableReturnsCompleteOrdinalCatalogAndReadsProtectedStateOnce()
    {
        var (service, provider) = CreateConfiguredService();
        AddProfile(provider, "z", "Zulu", "driving", 1);
        var first = AddProfile(provider, "a", "Alpha", "walking", 1);
        AddProfile(provider, "bad", "Ignored", "walking", 0, active: false);
        await Db.SaveChangesAsync();
        var reads = 0;
        service.CredentialReadOverride = () => { reads++; return true; };

        var result = await service.DiscoverAsync("owner", default);

        Assert.Equal("available", result.Outcome);
        Assert.Equal(2, result.Profiles.Count);
        Assert.Equal(first.Id, result.Profiles[0].TransportProfileId);
        Assert.Equal(["Alpha", "Zulu"], result.Profiles.Select(item => item.DisplayName));
        Assert.True(DiscoveryCatalogIdentity.IsValid(result.DiscoveryCatalogIdentity));
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData(100, "available", 100)]
    [InlineData(101, "profile-limit-exceeded", 0)]
    public async Task CatalogLimitNeverReturnsPartialAuthority(int count, string outcome, int returned)
    {
        var (service, provider) = CreateConfiguredService();
        for (var index = 0; index < count; index++)
            AddProfile(provider, $"p{index:D3}", $"Profile {index:D3}", "walking", index);
        await Db.SaveChangesAsync();

        var result = await service.DiscoverAsync("owner", default);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(returned, result.Profiles.Count);
        Assert.Equal(outcome == "available", result.DiscoveryCatalogIdentity is not null);
    }

    [Fact]
    public async Task AuthorityDriftAfterReadReturnsNoCatalogOrIdentity()
    {
        var (service, provider) = CreateConfiguredService();
        AddProfile(provider, "walk", "Walk", "walking", 0);
        await Db.SaveChangesAsync();
        service.AfterCredentialReadAsync = async token =>
        {
            provider.ConfigurationVersion++;
            provider.VerifiedConfigurationVersion = provider.ConfigurationVersion;
            await Db.SaveChangesAsync(token);
        };

        var result = await service.DiscoverAsync("owner", default);

        Assert.Equal("temporarily-unavailable", result.Outcome);
        Assert.Empty(result.Profiles);
        Assert.Null(result.DiscoveryCatalogIdentity);
    }

    [Fact]
    public async Task ZeroEligibleProfilesReturnsBoundedEmptyOutcome()
    {
        var (service, _) = CreateConfiguredService();
        await Db.SaveChangesAsync();

        var result = await service.DiscoverAsync("owner", default);

        Assert.Equal("no-eligible-profiles", result.Outcome);
        Assert.Empty(result.Profiles);
        Assert.Null(result.DiscoveryCatalogIdentity);
    }

    [Fact]
    public async Task ServerDefaultRejectedByMobileIsNotAdvertised()
    {
        var (service, provider) = CreateConfiguredService(serverDefault: true);
        AddProfile(provider, "walk", "Walk", "walking", 0);
        await Db.SaveChangesAsync();

        var result = await service.DiscoverAsync("owner", default);

        Assert.Equal("no-authority", result.Outcome);
        Assert.Empty(result.Profiles);
    }

    [Fact]
    public async Task UnexpectedProtectedReadFailureIsBounded()
    {
        var (service, provider) = CreateConfiguredService();
        AddProfile(provider, "walk", "Walk", "walking", 0);
        await Db.SaveChangesAsync();
        service.CredentialReadOverride = () => throw new Exception("sensitive diagnostic");

        var result = await service.DiscoverAsync("owner", default);

        Assert.Equal("temporarily-unavailable", result.Outcome);
        Assert.Empty(result.Profiles);
        Assert.Null(result.DiscoveryCatalogIdentity);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        var (service, provider) = CreateConfiguredService();
        AddProfile(provider, "walk", "Walk", "walking", 0);
        await Db.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        service.AfterCredentialReadAsync = token =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DiscoverAsync("owner", cancellation.Token));
    }

    [Fact]
    public async Task DisposedDatabaseFailureIsBounded()
    {
        var (service, _) = CreateConfiguredService();
        Db.Dispose();

        var result = await service.DiscoverAsync("owner", default);

        Assert.Equal("temporarily-unavailable", result.Outcome);
        Assert.Empty(result.Profiles);
        Assert.Null(result.DiscoveryCatalogIdentity);
    }

    private ApplicationDbContext Db { get; set; } = null!;

    private (MobileRoutingProfileDiscoveryService Service, RoutingProviderConfiguration Provider) CreateConfiguredService(
        bool serverDefault = false)
    {
        Db = CreateDbContext();
        var provider = new RoutingProviderConfiguration
        {
            Id = Guid.NewGuid(), DisplayName = "Routing", AdapterType = serverDefault
                ? RoutingAdapterType.OsrmCompatible : RoutingAdapterType.Geoapify,
            Enabled = true, BaseEndpoint = "https://routing.example", ConfigurationVersion = 3,
            VerifiedConfigurationVersion = 3, PersonalRoutingAccess = PersonalRoutingAccess.Disabled
        };
        var protection = new EphemeralDataProtectionProvider();
        var personalCredentials = new PersonalProviderCredentialService(protection);
        Db.Add(provider);
        if (serverDefault)
        {
            Db.Add(UserRoutingConfiguration.CreateServerDefault("owner"));
        }
        else
        {
            var personal = PersonalLocationProviderProfile.Create("owner", PersonalLocationProvider.Geoapify);
            personalCredentials.Replace(personal, "secret");
            personal.RoutingAuthorized = true;
            personal.RoutingVerification = PersonalProviderVerification.Verified;
            personal.RoutingVerifiedCredentialGeneration = personal.CredentialGeneration;
            personal.RoutingVerifiedConfigurationGeneration = personal.RoutingGeneration;
            Db.AddRange(personal,
                new PersonalLocationProviderSelection { UserId = "owner", RoutingProviderKey = "geoapify" });
        }
        Db.Add(new ApplicationSettings { Id = 1, ExternalRouteGenerationEnabled = true,
            ExternalRouteGenerationVersion = 7, ActiveRoutingProviderConfigurationId = serverDefault ? provider.Id : null });
        return (new(Db, new(protection), new(protection), personalCredentials), provider);
    }

    private TransportProfile AddProfile(RoutingProviderConfiguration provider, string key, string label,
        string category, int order, bool active = true)
    {
        var profile = new TransportProfile
        {
            Id = Guid.NewGuid(), Key = key, Label = label, Category = category, SortOrder = order, IsActive = active
        };
        var mapping = new RoutingProviderProfileMapping
        {
            RoutingProviderConfigurationId = provider.Id, RoutingProviderConfiguration = provider,
            TransportProfileId = profile.Id, TransportProfile = profile, OsrmProfile = "walk"
        };
        provider.ProfileMappings.Add(mapping);
        Db.Add(profile);
        return profile;
    }
}
