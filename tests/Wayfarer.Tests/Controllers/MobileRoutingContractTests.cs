using Wayfarer.Areas.Api.Controllers;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Locks the additive provider-neutral mobile routing request boundary.</summary>
public sealed class MobileRoutingContractTests
{
    [Fact]
    public void ControllerExposesAuthenticatedProfileDiscoveryOwner()
    {
        var action = typeof(MobileRoutingController).GetMethod("Profiles");

        Assert.NotNull(action);
    }

    [Fact]
    public void RouteRequestAcceptsOptionalDiscoveryAuthorityIdentity()
    {
        var property = typeof(MobileRouteRequest).GetProperty("AuthorityIdentity");

        Assert.NotNull(property);
        Assert.Equal(typeof(string), property.PropertyType);
    }

    [Fact]
    public void RouteServiceOwnsPreAdmissionDiscoveryIdentityFence()
    {
        var route = typeof(Wayfarer.Services.ExternalRouting.MobileRoutingService).GetMethod("RouteAsync");

        Assert.Contains(route!.GetParameters(), parameter => parameter.Name == "authorityIdentity");
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
        Assert.DoesNotContain("Mode", names);
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
}
