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

        Assert.Equal("57617966617265722e4d6f62696c65526f7574696e67446973636f76657279436174616c6f6700011000000009617661696c61626c65110000000120102132435465768798a9bacbdcedfe0f210000000457616c6b220000000477616c6b230000000777616c6b696e67", Convert.ToHexString(bytes).ToLowerInvariant());
        Assert.Equal("9e0384292dcfb8400f3d8ba677e622677887e4e30c77bca3a3b0e908b327047c", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        Assert.Equal("v1.ngOEKS3PuEAPPYumd-YiZ3iH5OMMd7yjo7DpCLMnBHw", DiscoveryCatalogIdentity.Compute(projection));
    }

    [Fact]
    public void SelectedAuthorityFixtureMatchesLiteralVector()
    {
        var projection = new MobileRoutingSelectedProfileAuthorityProjection("owner", 7, 1,
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), 2, true, 9, 10, 3, 4,
            Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"), "walk", true,
            5, true, 1, 4, 3, 9);
        var bytes = SelectedProfileAuthorityIdentity.Encode(projection);

        Assert.Equal("57617966617265722e4d6f62696c65526f7574696e6753656c656374656450726f66696c65417574686f72697479000110000000056f776e6572110000000712000000011300112233445566778899aabbccddeeff14000000021501160000000917000000000000000a18000000031900000000000000041a102132435465768798a9bacbdcedfe0f1b0000000477616c6b1c011d000000051e011f00000001200100000004210100000003220100000009", Convert.ToHexString(bytes).ToLowerInvariant());
        Assert.Equal("93602521f0c47f6e6caa08ffb5aa1c21f9b3bd6e1e8d9bf4df9540570030d7e2", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        Assert.Equal("v1.k2AlIfDEf25sqgj_taocIfmzvW4ejZv035VAVwAw1-I", SelectedProfileAuthorityIdentity.Compute(projection));
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
