using System.Text;
using Wayfarer.Services.LocationMutations;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Approved fixed vectors and strict wire semantics at the production parser.</summary>
public sealed class LocationMutationProtocolTests
{
    private const string Body = """
        {"contract":"location-mutation-1","normalization":"location-values-1","adapter":"owner","baseHandle":"example-handle","kind":"update","expected":{"uuid":{"state":"known","value":null},"notesHtml":{"state":"known","value":null}},"changes":{"notes":{"op":"clear"}}}
        """;

    /// <summary>Correlates exact bytes, including Unicode and trailing whitespace, before the first send.</summary>
    [Theory]
    [InlineData(0, "sha256:961954f23232166d67d4495d4f7370b4d8be7acb47c8d90bdff401d91a3322ce")]
    [InlineData(1, "sha256:b7c9900c92a43eeed2934fa334a39ce7eb1b7fc41f94bed3b4d4e1b13e2ebdbd")]
    [InlineData(2, "sha256:3dad01e294e9c69bac41ba4afa3e6730a0a03ae1e2f47ac7ff4befc60ffea9d5")]
    public void FramedVectors(int variant, string expected)
    {
        var raw = variant == 1 ? Body.Replace("{\"op\":\"clear\"}", "{\"op\":\"set\",\"html\":\"<p>Αθήνα</p>\"}") : Body + (variant == 2 ? "\n" : "");
        var bytes = Encoding.UTF8.GetBytes(raw);
        Assert.Equal(expected, LocationMutationCommand.Fingerprint("owner-a", 421,
            Guid.Parse("83e8095d-45b0-4dc7-a267-89a4ee119d67"), Guid.Parse("24cddbea-057b-4f78-8ccf-b0aa254aa011"), "owner", bytes));
        Assert.Equal("update", LocationMutationCommand.Parse(bytes).Kind);
    }

    /// <summary>Unknown/duplicate/contradictory objects cannot acquire command authority.</summary>
    [Theory]
    [InlineData("\"op\":\"clear\"", "\"op\":\"clear\",\"html\":null")]
    [InlineData("\"op\":\"clear\"", "\"op\":\"clear\",\"op\":\"set\"")]
    [InlineData("\"state\":\"known\",\"value\":null", "\"state\":\"known\"")]
    [InlineData("\"adapter\":\"owner\"", "\"Adapter\":\"owner\"")]
    [InlineData("\"op\":\"clear\"", "\"op\":\"set\",\"html\":\"\\ud800\"")]
    public void MalformedCommandsReject(string from, string to) =>
        Assert.Throws<MutationProtocolException>(() => LocationMutationCommand.Parse(Encoding.UTF8.GetBytes(Body.Replace(from, to))));

    /// <summary>Before-epoch conversion must truncate toward zero rather than dropping decimal digits.</summary>
    [Theory]
    [InlineData("2026-10-29T08:15:00.123456789Z", "2026-10-29T08:15:00.123456Z")]
    [InlineData("1999-12-31T23:59:59.999999900Z", "2000-01-01T00:00:00Z")]
    public void OriginalTimeUsesStorageConversion(string original, string stored) =>
        Assert.Equal(LocationMutationCommand.Instant(stored, false), LocationMutationCommand.Instant(original, true));
}
