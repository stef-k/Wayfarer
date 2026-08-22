using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Parsers;

public class WayfarerGeoJsonRecoveryIdTests
{
    [Fact]
    public async Task ParseAsync_CanonicalRecoveryId_PersistsAuthenticatedUserIdentity()
    {
        var key = Guid.NewGuid();
        var json = "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[22.2,40.1]},\"properties\":{\"TimestampUtc\":\"2026-08-22T10:00:00Z\",\"IdempotencyKey\":\"" + key.ToString("D") + "\"}}]}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var parser = new WayfarerGeoJsonParser(NullLogger<WayfarerGeoJsonParser>.Instance);

        var location = Assert.Single(await parser.ParseAsync(stream, "authenticated-user"));

        Assert.Equal("authenticated-user", location.UserId);
        Assert.Equal(key, location.IdempotencyKey);
    }
}
