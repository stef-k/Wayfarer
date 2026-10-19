using NetTopologySuite.Geometries;
using Wayfarer.Models.Dtos;
using Location = Wayfarer.Models.Location;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Deterministic clock/provenance cases supplement the routed persistence and effect evidence.</summary>
public sealed class LocationPresenceTests
{
    /// <summary>New fix-clock syntax requires an offset; legacy event clocks retain their separate existing binder.</summary>
    [Theory]
    [InlineData("2026-10-19T12:00:00Z", true)]
    [InlineData("2026-10-19T14:00:00+02:00", true)]
    [InlineData("2026-10-19T12:00:00", false)]
    public void ObservationClock_RequiresExplicitOffset(string clock, bool valid)
    {
        var json = "{\"ObservedAtUtc\":\"" + clock + "\"}";
        if (!valid)
            Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<GpsLoggerLocationDto>(json));
        else
            Assert.Equal(12, System.Text.Json.JsonSerializer.Deserialize<GpsLoggerLocationDto>(json)!.ObservedAtUtc!.Value.Hour);
    }

    /// <summary>History admission is independent; only defensible recent sensor observations authorize presence.</summary>
    [Theory]
    [InlineData("sensor", 0, 0, 0, 5, true)]
    [InlineData("sensor", 300000, 300000, 0, 5, true)]
    [InlineData("sensor", 300001, 300001, 0, 5, false)]
    [InlineData("sensor", 86400000, 86400000, 0, 5, false)]
    [InlineData("sensor", 1000, 1000, 86400000, 5, false)]
    [InlineData("sensor", 0, null, 0, 5, false)]
    [InlineData("sensor", 1000, 500, 0, 5, false)]
    [InlineData("sensor", -1, 0, 0, 5, false)]
    [InlineData("sensor", 0, -1, 0, 5, false)]
    [InlineData("sensor", 0, 0, -1, 5, false)]
    [InlineData("sensor", 0, 0, 0, 0, false)]
    [InlineData("sensor", 0, 0, 0, -1, false)]
    [InlineData("manual-map", 0, 0, 0, 5, false)]
    [InlineData(null, 0, 0, 0, 5, false)]
    public void Evidence_RequiresKnownAgeChronologyAndPositiveWindow(string? origin, int fixAge,
        int? elapsedAge, int deliveryAge, int window, bool eligible)
    {
        var receipt = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        var eventTime = receipt.AddMilliseconds(-deliveryAge);
        var row = new Location { UserId = "owner", TimeZoneId = "UTC", Coordinates = new Point(20, 10),
            Timestamp = receipt, LocalTimestamp = eventTime, CoordinateOrigin = origin,
            ObservedAtUtc = eventTime.AddMilliseconds(-fixAge), ObservedAgeAtEventMs = elapsedAge };
        var observed = LocationPresence.CheckInObservation(row, window);
        Assert.Equal(eligible, observed.HasValue);
        row.PresenceObservedAtUtc = observed;
        Assert.Equal(eligible, LocationPresence.IsLive(row, window, receipt));
        Assert.False(LocationPresence.IsLive(row, window, receipt.AddMinutes(6)));
        Assert.False(LocationPresence.IsLive(row, 0, receipt));
    }
}
