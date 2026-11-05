using Microsoft.AspNetCore.Http;
using NodaTime;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Private civil scope and unsampled connection contracts, independent of transport or storage.</summary>
public sealed class TimelineCalendarTests
{
    /// <summary>Today always comes from the frozen viewer zone; capture has its own navigation horizon.</summary>
    [Fact]
    public void CalendarSeedsClampsAndKeepsDistinctHorizons()
    {
        var now = Instant.FromUtc(2026, 10, 25, 15, 30);
        var scope = Parse("?viewerZone=US%2FPacific", now);
        Assert.Equal("capture", scope.Query.Scope.TimeBasis);
        Assert.Equal("America/Los_Angeles", scope.Query.Zone.Id);
        Assert.Equal(new DateOnly(2026, 10, 25), scope.Date);
        Assert.Equal(new DateOnly(2026, 10, 26), scope.Horizon(now));
        var january = Parse("?viewerZone=UTC&dateType=day&year=2024&month=1&day=31", now);
        Assert.Equal(new DateOnly(2024, 2, 29), january.Move("month", 1)!.Date);
        Assert.Equal(new DateOnly(2025, 2, 28), january.Move("month", 1)!.Move("year", 1)!.Date);
        Assert.Throws<HistoryRequestException>(() => Parse("?viewerZone=UTC&owner=foreign", now));
        Assert.Throws<HistoryRequestException>(() => Parse("?viewerZone=UTC&timeBasis=viewer&year=2101", now));
    }

    /// <summary>Excluded/unknown intermediates and complete equal-time disagreement break touching edges only.</summary>
    [Fact]
    public void BarriersAndCompleteTiesRetainQualifiedIslands()
    {
        var reducer = new TimelineConnections(new(7200, null), false);
        reducer.Add(Row(0, true)); reducer.Add(Row(60, false)); reducer.Add(Row(120, true));
        reducer.Add(Row(180, true)); reducer.Add(Row(180, true)); reducer.Add(Row(240, true));
        reducer.Add(Row(300, true)); reducer.Add(Row(300, true, 2)); reducer.Add(Row(360, true));
        reducer.Add(Row(420, true));
        var result = reducer.Finish(false);
        Assert.Equal(3, result.EdgeCount);
        Assert.Equal("complete", result.Coverage);
        var incomplete = new TimelineConnections(new(7200, null), false);
        incomplete.Add(Row(0, true)); incomplete.Add(Row(60, null)); incomplete.Add(Row(120, true)); incomplete.Add(Row(180, true));
        Assert.Equal("partial", incomplete.Finish(false).Coverage);
    }

    /// <summary>Inclusive gap/speed, finite extremes and seam drawing leave the original analytical edge unchanged.</summary>
    [Fact]
    public void RulesAndGeometryDoNotChangeDistance()
    {
        var a = Row(0, true, 179.9); var b = Row(7200, true, -179.9);
        var distance = TimelineConnections.Distance(a, b);
        var plain = new TimelineConnections(new(7200, distance / 7200), false);
        plain.Add(a); plain.Add(b);
        var drawn = new TimelineConnections(new(double.MaxValue, double.MaxValue), true);
        drawn.Add(a); drawn.Add(b);
        Assert.Equal(distance, plain.Finish(false).DistanceMetres);
        var path = drawn.Finish(false);
        Assert.Equal(distance, path.DistanceMetres);
        Assert.Equal(2, path.Geometry!.Count);
        Assert.Equal("unknown", new TimelineConnections(new(7200, null), true).Finish(true).Coverage);
        var tooFast = new TimelineConnections(new(7200, double.Epsilon), false);
        tooFast.Add(a); tooFast.Add(b); Assert.Equal(0, tooFast.Finish(false).EdgeCount);
    }

    /// <summary>Geometry overflow keeps a completed summary and stops retaining vertices; Off allocates none.</summary>
    [Fact]
    public void GeometryOverflowNeverReturnsAPrefixPath()
    {
        var reducer = new TimelineConnections(new(7200, null), true);
        for (var i = 0; i < 25000; i++) reducer.Add(Row(i, true, i % 2));
        var result = reducer.Finish(false);
        Assert.Equal(24999, result.EdgeCount);
        Assert.Equal("complete", result.Coverage);
        Assert.Equal("unavailable", result.GeometryState);
        Assert.Null(result.Geometry);
    }

    private static TimelineSourceRow Row(int seconds, bool? match, double longitude = 0) =>
        new(seconds, new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds), match, longitude, 0);

    private static TimelineCalendarScope Parse(string query, Instant now)
    {
        var context = new DefaultHttpContext(); context.Request.QueryString = new(query);
        return TimelineCalendarScope.Parse(context.Request.Query, false, now);
    }
}
