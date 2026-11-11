using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Exact synthetic App exports from #973 enter the existing production worker unchanged.</summary>
public sealed partial class LocationExportRoundTripPostgresTests
{
    /// <summary>Both roles/formats complete under destination authority while retaining shared values and foreign winners.</summary>
    [PostgresTheory]
    [InlineData("original", LocationImportFileType.Csv)]
    [InlineData("original", LocationImportFileType.WayfarerGeoJson)]
    [InlineData("effective", LocationImportFileType.Csv)]
    [InlineData("effective", LocationImportFileType.WayfarerGeoJson)]
    public async Task AppRecoverySpecimen_CompletesWithAuthenticatedDestination(string kind, LocationImportFileType format)
    {
        var destination = await fixture.CreateUserAsync();
        var other = await fixture.CreateUserAsync();
        var extension = format == LocationImportFileType.Csv ? "csv" : "geojson";
        var payload = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Recovery", $"app-{kind}.{extension}"));
        var key = Guid.Parse(kind == "original" ? "1f8cb015-5898-4427-9c85-7628e660a171" : "2f8cb015-5898-4427-9c85-7628e660a171");
        var foreign = CreateLocation(other.Id, key);
        const string activityName = "Walking · Περίπατος";
        await using var db = fixture.CreateContext();
        var activity = await db.ActivityTypes.SingleOrDefaultAsync(row => row.Name == activityName);
        var ownsActivity = activity == null;
        activity ??= new ActivityType { Name = activityName };
        if (ownsActivity) db.ActivityTypes.Add(activity);
        db.Locations.Add(foreign);
        await db.SaveChangesAsync();
        try
        {
            var before = DateTime.UtcNow.AddSeconds(-1);
            var import = await ImportAsync(destination.Id, format, payload);
            var parsed = Assert.Single(await ParseAsync(destination.Id, format, payload));
            var saved = await db.Locations.AsNoTracking().SingleAsync(row => row.UserId == destination.Id);
            Assert.Equal((1, 1, 0, 0), (import.TotalRecords, import.LastProcessedIndex, import.SkippedDuplicates, import.SkippedDeleted));
            Assert.False(import.EnrichmentRequested);
            Assert.False(import.EnrichmentHandoffPending);
            Assert.Null(import.FailureCode);
            Assert.Equal(destination.Id, saved.UserId);
            Assert.NotEqual(foreign.Id, saved.Id);
            Assert.Equal(key, saved.IdempotencyKey);
            Assert.Equal("Manual", saved.LocationType);
            Assert.Equal(activity.Id, saved.ActivityTypeId);
            // Each original parse assigns its own receipt; the stored receipt is checked independently below.
            if (kind == "original") parsed.Timestamp = saved.Timestamp;
            AssertMetadata(parsed, saved);
            Assert.Equal(("1960-01-02T15:00:00.123456789", "GMT+03:00", 10800),
                (saved.OriginalLocalTimestamp, saved.OriginalTimeZoneId, saved.OriginalUtcOffsetSeconds));
            Assert.Equal("1960-01-02T11:59:58.111111Z", ObservationEvidence.Format(saved.ObservedAtUtc!.Value));
            Assert.Equal("1960-01-02T12:00:04.999999Z", ObservationEvidence.Format(saved.SubmittedAtUtc!.Value));
            Assert.Equal("1960-01-02T12:00:00.123456Z", ObservationEvidence.Format(new(saved.LocalTimestamp)));
            if (kind == "original") Assert.InRange(saved.Timestamp, before, DateTime.UtcNow);
            else Assert.Equal("1960-01-02T12:01:00.654321Z", ObservationEvidence.Format(new(saved.Timestamp)));
            var retained = await db.Locations.AsNoTracking().SingleAsync(row => row.Id == foreign.Id);
            Assert.Equal(other.Id, retained.UserId);
            Assert.Equal((foreign.IdempotencyKey, foreign.Timestamp, foreign.Notes, foreign.PresenceObservedAtUtc),
                (retained.IdempotencyKey, retained.Timestamp, retained.Notes, retained.PresenceObservedAtUtc));
        }
        finally
        {
            // The shared fixture owns the users/jobs. Remove only our Locations and newly seeded catalog row.
            await db.Locations.Where(row => row.UserId == destination.Id || row.UserId == other.Id).ExecuteDeleteAsync();
            if (ownsActivity)
                await db.ActivityTypes.Where(row => row.Id == activity.Id && !db.Locations.Any(location => location.ActivityTypeId == row.Id))
                    .ExecuteDeleteAsync();
        }
    }
}
