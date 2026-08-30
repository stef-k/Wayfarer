using System.Security.Cryptography;
using Wayfarer.Services.ExternalRouting;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Pins the public v1 routing-authority canonical byte contract.</summary>
public sealed class MobileRoutingAuthorityIdentityTests
{
    [Fact]
    public void FramingMatchesLiteralVector()
    {
        var bytes = MobileRoutingAuthorityIdentity.EncodeFraming();

        Assert.Equal("57617966617265722e4d6f62696c65526f7574696e67417574686f726974790001",
            Convert.ToHexString(bytes).ToLowerInvariant());
        Assert.Equal("c14f20f5daa4b0c7cd8fec56a00da6a38f94a244a8aba23fd075303c474bd677",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    [Fact]
    public void CompleteFixtureMatchesLiteralBytesHashAndIdentity()
    {
        var providerId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var projection = new MobileRoutingAuthorityProjection(true, 7, 0x02,
            null, null, null, null, null, null, null, null, null,
            providerId, 3, false, null, null, null, true,
            providerId, true, 2, 9, 9, 2,
            [new(Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"), true, -2, "Walk", "walk", "walking")]);
        const string expected = "57617966617265722e4d6f62696c65526f7574696e67417574686f7269747900011001110000000000000007120213001400150016001700180019001a001b001c0100112233445566778899aabbccddeeff1d0100000000000000031e01001f002000210022012300112233445566778899aabbccddeeff2401250000000226000000000000000927010000000000000009280000000229000000013000112233445566778899aabbccddeeff31102132435465768798a9bacbdcedfe0f320133fffffffe340000000457616c6b350000000477616c6b360000000777616c6b696e67";

        var bytes = MobileRoutingAuthorityIdentity.Encode(projection);

        Assert.Equal(227, bytes.Length);
        Assert.Equal(expected, Convert.ToHexString(bytes).ToLowerInvariant());
        Assert.Equal("a5224738d65104caaaa9818451c14737460d8376a879650e784e2b354037e75a",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        Assert.Equal("v1.pSJHONZRBMqqqYGEUcFHN0YNg3aoeWUOeE4rNUA351o",
            MobileRoutingAuthorityIdentity.Compute(projection));
    }

    [Theory]
    [InlineData("v1.pSJHONZRBMqqqYGEUcFHN0YNg3aoeWUOeE4rNUA351o", true)]
    [InlineData("", false)]
    [InlineData(" v1.pSJHONZRBMqqqYGEUcFHN0YNg3aoeWUOeE4rNUA351o", false)]
    [InlineData("v2.pSJHONZRBMqqqYGEUcFHN0YNg3aoeWUOeE4rNUA351o", false)]
    [InlineData("v1.pSJHONZRBMqqqYGEUcFHN0YNg3aoeWUOeE4rNUA351=", false)]
    public void SyntaxValidationIsExact(string value, bool expected) =>
        Assert.Equal(expected, MobileRoutingAuthorityIdentity.IsValid(value));
}
