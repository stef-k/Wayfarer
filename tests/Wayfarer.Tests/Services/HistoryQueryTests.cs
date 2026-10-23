using Microsoft.AspNetCore.Http;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Strict transport and wall-boundary semantics at the normalized product seam.</summary>
public sealed class HistoryQueryTests
{
    private const string Common = "?timeBasis=capture&timeMode=daily&viewerZone=Europe%2FAthens&fromDate=2025-10-10&throughDate=2025-10-12&fromTime=22:00&untilTime=02:00";

    /// <summary>Overnight dates name start days, including the final spill and excluding the first early hours.</summary>
    [Theory]
    [InlineData("2025-10-10T01:00:00", false)]
    [InlineData("2025-10-10T22:00:00", true)]
    [InlineData("2025-10-11T02:00:00", false)]
    [InlineData("2025-10-13T01:59:59.999999", true)]
    [InlineData("2025-10-13T02:00:00", false)]
    public void OvernightUsesStartDays(string local, bool expected)
    {
        var query = Parse(Common);
        Assert.Equal(expected, query.ContainsWall(DateTime.Parse(local)));
    }

    /// <summary>Wrong shapes, precision, Unicode, controls and irrelevant parameters fail before SQL.</summary>
    [Theory]
    [InlineData("&fromTime=01:00")]
    [InlineData("&owner=other")]
    [InlineData("&grid=4")]
    [InlineData("&q=%00")]
    [InlineData("&includeUnassigned=false")]
    [InlineData("&west=NaN&south=0&east=10&north=10")]
    [InlineData("&startUtc=2025-01-01T00:00:00Z")]
    public void InvalidCombinationsAreRejected(string suffix) => Assert.Throws<HistoryRequestException>(() => Parse(Common + suffix));

    /// <summary>Equivalent phrases use I918-A's field normalizer, with response controls outside scope.</summary>
    [Fact]
    public void NormalizationAndScopeIgnoreResponseControls()
    {
        var first = Parse(Common + "&q=caf%C3%A9%20%20station&pageSize=1");
        var second = Parse(Common + "&q=CAFE%20STATION&pageSize=500&resultSet=unresolvedCandidates");
        Assert.Equal("CAFE STATION", first.Scope.Q);
        Assert.Equal(first.Scope, second.Scope);
        Assert.Equal(1, first.PageSize);
    }

    /// <summary>Continuous intervals retain daytime; whole days reject an unrepresentable next boundary.</summary>
    [Fact]
    public void ContinuousAndDayBoundsAreExact()
    {
        var query = Parse("?timeBasis=viewer&timeMode=interval&viewerZone=UTC&fromLocal=2025-01-01T22:00:00&untilLocal=2025-01-03T02:00:00");
        Assert.True(query.ContainsWall(new DateTime(2025, 1, 2, 12, 0, 0)));
        Assert.False(query.ContainsWall(new DateTime(2025, 1, 3, 2, 0, 0)));
        Assert.Throws<HistoryRequestException>(() => Parse("?timeBasis=viewer&timeMode=days&viewerZone=UTC&fromDate=9999-12-31&throughDate=9999-12-31"));
    }

    /// <summary>RFC3339 offsets normalize exactly, while extra precision and unrepresentable instants are rejected.</summary>
    [Fact]
    public void ExplicitOffsetsAndPrecisionPreserveMembership()
    {
        var query = Parse("?timeBasis=utc&timeMode=interval&viewerZone=UTC&startUtc=2025-01-02T00:00:00%2B15:00&endUtc=2025-01-02T00:00:00%2B14:00");
        Assert.Equal("2025-01-01T09:00:00.000000Z", query.Scope.StartUtc);
        Assert.Throws<HistoryRequestException>(() => Parse("?timeBasis=utc&timeMode=interval&viewerZone=UTC&startUtc=2025-01-01T00:00:00.0000001Z&endUtc=2025-02-01T00:00:00Z"));
        Assert.Throws<HistoryRequestException>(() => Parse("?timeBasis=utc&timeMode=interval&viewerZone=UTC&startUtc=0001-01-01T00:00:00%2B01:00&endUtc=2025-02-01T00:00:00Z"));
        Assert.Equal(new DateTime(2025, 1, 1, 12, 0, 0), Parse("?timeBasis=capture&timeMode=interval&viewerZone=UTC&fromLocal=2025-01-01T12:00&untilLocal=2025-01-01T13:00").Start);
    }

    /// <summary>Final-year recurring windows require a next day only when they actually spill overnight.</summary>
    [Fact]
    public void LastYearDailyWindowsDoNotInventAnExtraDayBoundary()
    {
        var common = "?timeBasis=viewer&timeMode=daily&viewerZone=UTC&fromDate=9999-12-31&throughDate=9999-12-31&fromTime=11:00&untilTime=15:00";
        Assert.True(Parse(common).ContainsWall(new DateTime(9999, 12, 31, 12, 0, 0)));
        Assert.False(Parse(common).ContainsWall(new DateTime(9999, 12, 31, 15, 0, 0)));
        Assert.Throws<HistoryRequestException>(() => Parse(common.Replace("11:00", "22:00").Replace("15:00", "02:00")));
        Assert.True(Parse(common.Replace("9999-12-31", "9999-12-30").Replace("11:00", "22:00").Replace("15:00", "02:00"))
            .ContainsWall(new DateTime(9999, 12, 31, 1, 0, 0)));
    }

    /// <summary>Normalized recurring clocks can be reapplied directly, including six-digit fractions.</summary>
    [Fact]
    public void FractionalClockScopeEchoRoundTripsWithoutRounding()
    {
        var query = Parse(Common.Replace("22:00", "22:00:00.123456"));
        Assert.Equal("22:00:00.123456", query.Scope.FromTime);
        var roundTrip = Parse(Common.Replace("22:00", query.Scope.FromTime!).Replace("02:00", query.Scope.UntilTime!));
        Assert.Equal(query.Scope, roundTrip.Scope);
    }

    /// <summary>Use the same query collection parser as the route, without a host or database.</summary>
    internal static HistoryQuery Parse(string value, bool overview = false)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(value);
        return HistoryQuery.Parse(context.Request.Query, overview);
    }
}
