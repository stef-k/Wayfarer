using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Services.ExternalRouting;
using System.Text.Json;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Locks the additive provider-neutral mobile routing request boundary.</summary>
public sealed class MobileRoutingContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ControllerExposesAuthenticatedProfileDiscoveryOwner()
    {
        var action = typeof(MobileRoutingController).GetMethod("Profiles");

        Assert.NotNull(action);
    }

    [Fact]
    public void DtosExposeSeparateCatalogAndSelectedAuthorityIdentities()
    {
        var discovery = typeof(Wayfarer.Services.ExternalRouting.MobileRoutingProfileDiscovery)
            .GetProperty("DiscoveryCatalogIdentity");
        var capability = typeof(Wayfarer.Services.ExternalRouting.MobileRoutingCapability);

        Assert.NotNull(discovery);
        Assert.NotNull(capability.GetProperty("DiscoveryCatalogIdentity"));
        Assert.NotNull(capability.GetProperty("SelectedProfileAuthorityIdentity"));
    }

    [Fact]
    public void RouteRequestAndServiceUseSelectedProfileAuthorityIdentity()
    {
        Assert.NotNull(typeof(MobileRouteRequest).GetProperty("SelectedProfileAuthorityIdentity"));
        var route = typeof(Wayfarer.Services.ExternalRouting.MobileRoutingService).GetMethods()
            .Single(method => method.Name == "RouteAsync" && method.GetParameters().Length == 6
                && method.GetParameters().Any(parameter => parameter.Name == "selectedProfileAuthorityIdentity"));

        Assert.Contains(route.GetParameters(), parameter => parameter.Name == "selectedProfileAuthorityIdentity");
    }

    [Fact]
    public void CapabilityAcceptsDiscoveryCatalogIdentityBeforeReturningMetadata()
    {
        var capability = typeof(MobileRoutingController).GetMethod("Capability");

        Assert.Contains(capability!.GetParameters(), parameter => parameter.Name == "discoveryCatalogIdentity");
    }

    [Theory]
    [InlineData("{\"origin\":{},\"destination\":{}}", null)]
    [InlineData("{\"origin\":{},\"destination\":{},\"selectedProfileAuthorityIdentity\":null}", null)]
    [InlineData("{\"origin\":{},\"destination\":{},\"selectedProfileAuthorityIdentity\":42}", "!invalid")]
    [InlineData("{\"origin\":{},\"destination\":{},\"selectedProfileAuthorityIdentity\":{}}", "!invalid")]
    public void RequestBindingKeepsLegacyNullAndCollapsesNonStringIdentity(string json, string? expected)
    {
        var request = JsonSerializer.Deserialize<MobileRouteRequest>(json, JsonOptions);

        Assert.Equal(expected, request!.SelectedProfileAuthorityIdentity);
    }

    [Fact]
    public void IdentityInputBoundRejectsExactSixtyFourAndOverWithoutTrimming()
    {
        Assert.False(SelectedProfileAuthorityIdentity.IsValid(new string('a', 64)));
        Assert.False(DiscoveryCatalogIdentity.IsValid(new string('a', 65)));
    }

    [Fact]
    public void RequestAcceptsStableProfileAndBoundedCoordinatesButNoProviderAuthority()
    {
        var names = typeof(MobileRouteRequest).GetProperties().Select(property => property.Name).ToHashSet();

        Assert.Contains("TransportProfileId", names);
        Assert.Contains("Origin", names);
        Assert.Contains("Destination", names);
        Assert.Contains("Anchors", names);
        Assert.DoesNotContain("Provider", names);
        Assert.Contains("ProviderMode", names);
        Assert.DoesNotContain("Endpoint", names);
        Assert.DoesNotContain("Credential", names);
    }

    [Fact]
    public void ResponseContainsNoSecretOrAdministratorEndpointMember()
    {
        var names = typeof(MobileRouteResponse).GetProperties().Select(property => property.Name).ToHashSet();

        Assert.DoesNotContain("Credential", names);
        Assert.DoesNotContain("ApiKey", names);
        Assert.DoesNotContain("Endpoint", names);
        Assert.DoesNotContain("ProtectedContext", names);
        Assert.Contains("ProviderConfigurationId", names);
        Assert.Contains("MappingIdentity", names);
        Assert.Contains("StorageMode", names);
    }

    [Fact]
    public void DiscoveryDtosExposeOnlyBoundedProviderNeutralAuthority()
    {
        var top = typeof(Wayfarer.Services.ExternalRouting.MobileRoutingProfileDiscovery).GetProperties()
            .Select(property => property.Name).ToArray();
        var profile = typeof(Wayfarer.Services.ExternalRouting.MobileRoutingProfile).GetProperties()
            .Select(property => property.Name).ToArray();

        Assert.Contains("Outcome", top);
        Assert.Contains("DiscoveryCatalogIdentity", top);
        Assert.Contains("Profiles", top);
        Assert.Contains("Provider", top);
        Assert.Contains("ProviderModes", top);
        Assert.Equal(["TransportProfileId", "DisplayName", "ModeKey", "Category"], profile);
        Assert.DoesNotContain(top.Concat(profile), name => name.Contains("Credential", StringComparison.Ordinal)
            || name.Contains("Endpoint", StringComparison.Ordinal) || name.Contains("Native", StringComparison.Ordinal)
            || name.Contains("Quota", StringComparison.Ordinal) || name.Contains("Generation", StringComparison.Ordinal));
    }
}
