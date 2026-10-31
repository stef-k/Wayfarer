using System.Text.Json;
using Wayfarer.Models.Planning;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves authored precision, absence and conservative reversal at the shared value boundary.</summary>
public sealed class PlanningValueTests
{
    private static readonly Guid EntryId = Guid.Parse("11111111-1111-4111-8111-111111111111");

    /// <summary>Builds one intention without adding a meaning or inferred clock.</summary>
    private static PlanningAggregate At(string precision, string local, string? zone = null, int? offset = null) =>
        new(1, [new(EntryId, new("at", new(precision, local, zone, offset)))]);

    /// <summary>Valid rough values retain literal components across the typed codec.</summary>
    [Theory]
    [InlineData("month", "0001-01")]
    [InlineData("date", "9999-12-31")]
    [InlineData("dateTime", "2027-03-02T10:15")]
    public void PrecisionRoundTrips(string precision, string local)
    {
        var value = At(precision, local);
        PlanningValidation.Validate(value, "place");
        Assert.True(PlanningCodec.Equal(value, PlanningCodec.Read(PlanningCodec.Write(value))));
        // MVC's ordinary Web options must use the same absent-qualifier wire shape as persistence.
        var response = JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(JsonDocument.Parse(PlanningCodec.Write(value)).RootElement.GetRawText(), response.GetRawText());
    }

    /// <summary>Malformed dates, seconds, invented zones and daylight gaps reject independently of the editor.</summary>
    [Theory]
    [InlineData("date", "2027-02-29", null, null)]
    [InlineData("dateTime", "2027-03-02T24:00", null, null)]
    [InlineData("dateTime", "2027-03-02T10:15:00", null, null)]
    [InlineData("date", "2027-03-02", "UTC", null)]
    [InlineData("dateTime", "2027-03-28T01:30", "Europe/London", null)]
    [InlineData("dateTime", "2027-03-02T10:15", "Europe/London", 3600)]
    public void MalformedValuesReject(string precision, string local, string? zone, int? offset) =>
        Assert.Throws<PlanningValidationException>(() => PlanningValidation.Validate(At(precision, local, zone, offset), "place"));

    /// <summary>A fold can remain unresolved or select either valid occurrence, never a false offset.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(3600)]
    public void FoldChoicesArePreserved(int? offset) =>
        PlanningValidation.Validate(At("dateTime", "2027-10-31T01:30", "Europe/London", offset), "place");

    /// <summary>End-only and mixed precision save; only ordering proved across every admissible endpoint rejects.</summary>
    [Fact]
    public void PartialAndReversalRules()
    {
        PlanningAggregate Period(PlanningValue? start, PlanningValue end) => new(1,
            [new(EntryId, new("period", Start: start == null ? null : new("known", start), End: new("known", end)))]);
        PlanningValidation.Validate(Period(null, new("date", "2027-03-15")), "place");
        PlanningValidation.Validate(Period(new("month", "2027-03"), new("date", "2027-03-02")), "place");
        Assert.Throws<PlanningValidationException>(() => PlanningValidation.Validate(
            Period(new("date", "2027-04-01"), new("month", "2027-03")), "place"));
        PlanningValidation.Validate(Period(new("dateTime", "2027-03-02T12:00", "Asia/Tokyo"),
            new("dateTime", "2027-03-02T04:00", "UTC")), "segment");
        PlanningValidation.Validate(Period(new("dateTime", "2027-03-02T12:00"),
            new("dateTime", "2027-03-02T04:00", "UTC")), "segment");
    }

    /// <summary>Unsupported and ambiguous wire content never silently disappears.</summary>
    [Theory]
    [InlineData("{\"format\":1,\"format\":1,\"entries\":[]}")]
    [InlineData("{\"format\":1,\"entries\":[],\"included\":true}")]
    public void StrictCodecRejects(string json) => Assert.Throws<JsonException>(() => PlanningCodec.Read(json));

    /// <summary>Unknown/flexible intentions and zero allocation retain exact optional content and repeated identities.</summary>
    [Fact]
    public void QualificationsAndRepeatedEntriesAreLossless()
    {
        var value = new PlanningAggregate(1, [new(EntryId, new("unknown"), "  First visit  ", "presence", 0),
            new(Guid.NewGuid(), new("flexible")), new(Guid.NewGuid(), new("period", End: new("known", new("month", "9999-12"))))]);
        PlanningValidation.Validate(value, "place");
        Assert.True(PlanningCodec.Equal(value, PlanningCodec.Read(PlanningCodec.Write(value))));
        Assert.Throws<PlanningValidationException>(() => PlanningValidation.Validate(new(1, [value.Entries[0], value.Entries[0]]), "place"));
        Assert.Throws<PlanningValidationException>(() => PlanningValidation.Validate(new(1,
            [new(EntryId, new("period", Start: new("unknown"), End: new("flexible")))]), "place"));
    }

    /// <summary>Trusted unchanged stored clocks survive rule drift with diagnostics; untrusted new clocks reject.</summary>
    [Fact]
    public void HistoricalGapIsRetainedWithoutExemptingNewInput()
    {
        var historical = At("dateTime", "2027-03-28T01:30", "Europe/London");
        PlanningValidation.Validate(historical, "place", historical);
        Assert.Single(PlanningValidation.Diagnostics(historical, "place"));
        Assert.Throws<PlanningValidationException>(() => PlanningValidation.Validate(historical, "place"));
        var mixed = new PlanningAggregate(1, [new(EntryId, new("period",
            Start: new("known", new("dateTime", "2027-04-01T10:00")), End: new("known", new("month", "2027-03"))))]);
        Assert.Throws<PlanningValidationException>(() => PlanningValidation.Validate(mixed, "place"));
    }
}
