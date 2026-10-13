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
    [InlineData("\ud800", false)]
    [InlineData("\udc00", false)]
    public void CanTransportPreservesText(string? value, bool expected) =>
        Assert.Equal(expected, ActivityCatalog.CanTransport(value));
}
