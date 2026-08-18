using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves generic geometry budgeting at the import and persistence orchestration seam.</summary>
public sealed class GenericKmlImportServiceTests : TestBase
{
    /// <summary>Proves only accepted final geometry reaches persistence and no semantic waypoint is created.</summary>
    [Fact]
    public async Task Import_OversizedGenericRoute_PersistsBudgetedGeometryAndNotice()
    {
        await using var database = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        database.Users.Add(user);
        await database.SaveChangesAsync();
        var service = new TripImportService(database, NullLogger<TripImportService>.Instance);

        var result = await service.ImportWayfarerKmlAsync(
            Stream(Kml(Route(2_001))), user.Id, TripImportMode.CreateNew);

        var segment = await database.Segments.AsNoTracking().Include(item => item.Waypoints)
            .SingleAsync(item => item.TripId == result.TripId);
        var geometry = Assert.IsType<LineString>(segment.RouteGeometry);
        Assert.InRange(geometry.NumPoints, 2, 500);
        Assert.Equal((0d, 40d), Pair(geometry.GetCoordinateN(0)));
        Assert.Equal((0.2d, 40d), Pair(geometry.GetCoordinateN(geometry.NumPoints - 1)));
        Assert.Empty(segment.Waypoints);
        Assert.Equal(EstimatedDurationSource.Automatic, segment.EstimatedDurationSource);
        Assert.Single(result.Notices);
    }

    /// <summary>Proves generic Upsert remains explicitly unsupported without tracking imported state.</summary>
    [Fact]
    public async Task Import_GenericUpsert_RejectsAndClearsTracker()
    {
        await using var database = CreateDbContext();
        var service = new TripImportService(database, NullLogger<TripImportService>.Instance);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportWayfarerKmlAsync(
            Stream(Kml("0,0 1,1")), "user", TripImportMode.Upsert));

        Assert.Equal("Generic KML upsert is not supported.", error.Message);
        Assert.Empty(database.ChangeTracker.Entries());
        Assert.Empty(database.Trips);
    }

    /// <summary>Proves a geometry budget failure leaves no imported graph and clears tracked state.</summary>
    [Fact]
    public async Task Import_InvalidGenericGeometry_IsAtomicAndClearsTracker()
    {
        await using var database = CreateDbContext();
        var service = new TripImportService(database, NullLogger<TripImportService>.Instance);

        var error = await Assert.ThrowsAsync<RouteGeometryBudgetException>(() => service.ImportWayfarerKmlAsync(
            Stream(Kml("0,0 181,1")), "user", TripImportMode.CreateNew));

        Assert.Equal("generic_kml_invalid_coordinate", error.Code);
        Assert.Empty(database.ChangeTracker.Entries());
        Assert.Empty(database.Trips);
        Assert.Empty(database.Segments);
    }

    /// <summary>Proves the import boundary prohibits DTD processing during its only XML parse.</summary>
    [Fact]
    public async Task Import_DtdInput_IsRejectedByHardenedParse()
    {
        await using var database = CreateDbContext();
        var service = new TripImportService(database, NullLogger<TripImportService>.Instance);
        var source = "<!DOCTYPE kml [<!ENTITY x 'private'>]><kml xmlns=\"http://www.opengis.net/kml/2.2\"><Document><name>&x;</name></Document></kml>";

        await Assert.ThrowsAnyAsync<Exception>(() => service.ImportWayfarerKmlAsync(
            Stream(source), "user", TripImportMode.CreateNew));

        Assert.DoesNotContain(database.ChangeTracker.Entries(), entry => entry.State == EntityState.Added);
        Assert.Empty(database.Trips);
    }

    /// <summary>Proves request cancellation reaches hardened XML loading and leaves no imported state.</summary>
    [Fact]
    public async Task Import_CancelledRequest_PropagatesWithoutImportedState()
    {
        await using var database = CreateDbContext();
        var service = new TripImportService(database, NullLogger<TripImportService>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportWayfarerKmlAsync(
            Stream(Kml(Route(2_001))), "user", TripImportMode.CreateNew, cancellation.Token));

        Assert.Empty(database.Trips);
        Assert.Empty(database.Segments);
    }

    private static string Kml(string coordinates) =>
        $"<kml xmlns=\"http://www.opengis.net/kml/2.2\"><Document><name>Generic</name><Placemark><name>walk</name><LineString><coordinates>{coordinates}</coordinates></LineString></Placemark></Document></kml>";
    private static string Route(int count) => string.Join(' ', Enumerable.Range(0, count).Select(index =>
        string.Create(CultureInfo.InvariantCulture, $"{index * 0.0001d:R},40")));
    private static MemoryStream Stream(string source) => new(Encoding.UTF8.GetBytes(source));
    private static (double Longitude, double Latitude) Pair(Coordinate coordinate) => (coordinate.X, coordinate.Y);
}
