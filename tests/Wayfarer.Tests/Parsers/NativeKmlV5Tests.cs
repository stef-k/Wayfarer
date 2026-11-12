using System.Text;
using System.Xml.Linq;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>Portable fields cannot disappear through default output, old envelopes or generic fallback.</summary>
public sealed class NativeKmlV5Tests
{
    private static readonly XNamespace K = "http://www.opengis.net/kml/2.2";

    /// <summary>Even empty unlicensed default output requires v5; explicit fields round trip independently.</summary>
    [Fact]
    public void DefaultAndPersonalCarryV5FieldsWithoutPlanningHints()
    {
        var trip = new Trip { Id = Guid.NewGuid(), Name = "Empty", ContributionPolicy = "CC0-1.0",
            Credits = [new("Bob", "Ideas", "Unfamiliar readable terms", Notices: "Keep this notice")] };
        foreach (var personal in new[] { false, true })
        {
            var xml = TripWayfarerKmlExporter.BuildKml(trip, personal);
            var parsed = Parse(xml).Document!;
            Assert.Equal(5, parsed.Version);
            Assert.Equal(trip.ContributionPolicy, parsed.ContributionPolicy);
            Assert.Equal(trip.Credits, parsed.Credits);
            Assert.DoesNotContain("Planning", xml);
        }
        trip.ContributionPolicy = null; trip.Credits = [];
        Assert.Equal(5, Parse(TripWayfarerKmlExporter.BuildKml(trip)).Document!.Version);
    }

    /// <summary>Downgraded, aliased, duplicated and ignored reserved claims must never become a successful generic import.</summary>
    [Theory]
    [InlineData("Credits")]
    [InlineData("ContentReuse")]
    [InlineData("Title")]
    [InlineData("credits")]
    [InlineData("WayfarerSchemaVersion")]
    public void ReservedClaimsInIgnoredXmlRefuse(string field) => Assert.Throws<FormatException>(() => Parse(
        $"<kml xmlns='{K}'><Document><Folder><ExtendedData><Data name='{field}'><value>[]</value></Data></ExtendedData></Folder></Document></kml>"));

    /// <summary>One shared codec preserves unfamiliar inherited terms and rejects unsafe or ambiguous links/data.</summary>
    [Fact]
    public void CreditsRemainReadableAndStrict()
    {
        var value = new TripCredit("Alice <script>", "Offline source", "Custom supplied text", Notices: "Notice", Modifications: "Adapted");
        Assert.Equal(value, TripCreditCodec.Read(TripCreditCodec.Write([value])).Single());
        Assert.Throws<FormatException>(() => TripCreditCodec.Read("[{\"contributor\":\"a\",\"Contributor\":\"b\",\"sourceTitle\":\"s\",\"licence\":\"l\"}]"));
        Assert.Throws<FormatException>(() => TripCreditCodec.Write([value with { SourceUrl = "javascript:alert(1)" }]));
        Assert.Throws<FormatException>(() => TripCreditCodec.Write([value with { SourceUrl = "https://user:pass@example.org" }]));
    }

    private static WayfarerKmlClassification Parse(string xml) =>
        WayfarerKmlParser.ClassifyAndParse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
}
