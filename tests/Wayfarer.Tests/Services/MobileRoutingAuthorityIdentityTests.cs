using System.Security.Cryptography;
using Wayfarer.Services.ExternalRouting;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Pins both public v1 identities to independent literal vectors.</summary>
public sealed class MobileRoutingAuthorityIdentityTests
{
    [Fact]
    public void DiscoveryCatalogFixtureMatchesLiteralVector()
    {
        var projection = new MobileRoutingDiscoveryCatalogProjection("available",
            [new(Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"), "Walk", "walk", "walking")]);
        var bytes = DiscoveryCatalogIdentity.Encode(projection);

        Assert.Equal("57617966617265722e4d6f62696c65526f7574696e67446973636f76657279436174616c6f6700011000000009617661696c61626c65110000000120102132435465768798a9bacbdcedfe0f210000000457616c6b220000000477616c6b230000000777616c6b696e671200000000", Convert.ToHexString(bytes).ToLowerInvariant());
        Assert.Equal("96fa84cbbbdaee4a27bf0540f595d5be392d0745b4a9a59e7349be57c0a7be5f", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        Assert.Equal("v1.lvqEy7va7konvwVA9ZXVvjktB0W0qaWec0m-V8Cnvl8", DiscoveryCatalogIdentity.Compute(projection));
    }

    [Fact]
    public void SelectedAuthorityUsesOnlyPersonalProviderAuthority()
    {
        var projection = new MobileRoutingSelectedProfileAuthorityProjection("owner", "geoapify",
            Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"), "walk", 7, 5, 4, 3, true, 1, 4, 3);
        var bytes = SelectedProfileAuthorityIdentity.Encode(projection);

        var identity = SelectedProfileAuthorityIdentity.Compute(projection);

        Assert.True(SelectedProfileAuthorityIdentity.IsValid(identity));
        Assert.Equal(identity, SelectedProfileAuthorityIdentity.Compute(projection));
        Assert.NotEqual(identity, SelectedProfileAuthorityIdentity.Compute(
            projection with { SelectionGeneration = projection.SelectionGeneration + 1 }));
        Assert.DoesNotContain("00112233", Convert.ToHexString(bytes), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("v1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", true)]
    [InlineData("", false)]
    [InlineData("v1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB", false)]
    [InlineData("v1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", false)]
    public void SyntaxValidationIsExact(string value, bool expected)
    {
        Assert.Equal(expected, DiscoveryCatalogIdentity.IsValid(value));
        Assert.Equal(expected, SelectedProfileAuthorityIdentity.IsValid(value));
    }
}
