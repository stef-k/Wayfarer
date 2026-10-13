using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Transport validation must never replace malformed text or invent name equivalence.</summary>
public sealed class ActivityCatalogTests
{
    /// <summary>Only NUL and malformed UTF-16 fail before the authoritative database call.</summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("GPS eBike Πεζοπορία 👩‍🚀", true)]
    [InlineData("A\u0001B", true)]
    [InlineData("A\0B", false)]
    public void CanTransportPreservesText(string? value, bool expected) =>
        Assert.Equal(expected, ActivityCatalog.CanTransport(value));
    /// <summary>Constructs unpaired surrogates at runtime because CLR attribute text cannot preserve them.</summary>
    [Fact]
    public void MalformedUnicodeNeverUsesReplacementEncoding()
    {
        Assert.False(ActivityCatalog.CanTransport(new string((char)0xd800, 1)));
        Assert.False(ActivityCatalog.CanTransport(new string((char)0xdc00, 1)));
        Assert.True(ActivityCatalog.CanTransport("\ufffd"));
    }
}
