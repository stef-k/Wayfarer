using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Preserves native/generic import and clone semantics while rejecting duplicate exact canonical Regions.</summary>
public sealed class CanonicalRegionImportClonePostgresTests(PostgresMigrationTestFixture fixture)
    : TestBase, IClassFixture<PostgresMigrationTestFixture>
{
    /// <summary>Duplicate imports return the existing 422 envelope before replacement or child/tag tracking.</summary>
    [PostgresTheory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task DuplicateImportRejectsBeforeMutation(int version, bool upsert)
    {
        var owner = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var original = new Trip { Id = Guid.NewGuid(), UserId = owner.Id, Name = "Original", Notes = "Original notes" };
        var region = new Region
        {
            Id = Guid.NewGuid(), Trip = original, UserId = owner.Id,
            Name = "Unassigned Places", DisplayOrder = 17, Notes = "Original Region"
        };
        db.Regions.Add(region);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await db.Trips.AsNoTracking().SingleAsync(trip => trip.Id == original.Id);
        var controller = new TripImportController(NullLogger<TripImportController>.Instance, db,
            new TripImportService(db, NullLogger<TripImportService>.Instance))
        {
            ControllerContext = new() { HttpContext = BuildHttpContextWithUser(owner.Id) }
        };
        var bytes = Encoding.UTF8.GetBytes(Kml(original.Id, version, "Unassigned Places", "Unassigned Places"));
        using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "duplicate.kml");

        var response = Assert.IsType<JsonResult>(await controller.Import(file,
            upsert ? TripImportMode.Upsert : TripImportMode.CreateNew));

        Assert.Equal(422, response.StatusCode);
        Assert.Empty(db.ChangeTracker.Entries());
        await using var verify = fixture.CreateContext();
        var after = await verify.Trips.AsNoTracking().SingleAsync(trip => trip.UserId == owner.Id);
        Assert.Equal((before.Id, before.Name, before.Notes, before.UpdatedAt), (after.Id, after.Name, after.Notes, after.UpdatedAt));
        var retained = await verify.Regions.SingleAsync(item => item.TripId == original.Id);
        Assert.Equal((region.Id, 17, "Original Region"), (retained.Id, retained.DisplayOrder, retained.Notes));
    }

    /// <summary>Canonical order/metadata survive import and clone; v2/v3 absence and near-names remain compatible.</summary>
    [PostgresTheory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public async Task ValidImportAndClonePreserveFallbackIdentitySemantics(int version, bool hasFallback)
    {
        var owner = await fixture.CreateUserAsync();
        var destination = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Kml(Guid.NewGuid(), version,
            hasFallback ? "Unassigned Places" : "unassigned places", "Ordinary")));
        var result = await new TripImportService(db, NullLogger<TripImportService>.Instance)
            .ImportWayfarerKmlAsync(stream, owner.Id, TripImportMode.CreateNew);
        var imported = await db.Trips.Include(trip => trip.Regions).SingleAsync(trip => trip.Id == result.TripId);
        Assert.Equal(2, imported.Regions.Count);
        Assert.Equal(hasFallback, imported.Regions.Any(region => region.Name == "Unassigned Places"));
        Assert.All(imported.Regions, region => Assert.Equal(owner.Id, region.UserId));
        imported.IsPublic = true;
        await db.SaveChangesAsync();
        var expected = imported.Regions.OrderBy(region => region.DisplayOrder)
            .Select(region => (region.Name, region.DisplayOrder, region.Notes)).ToArray();

        var cloneResult = await new TripCloneCoordinator(db).CloneAsync(imported.Id, destination.Id);

        var clone = await db.Trips.Include(trip => trip.Regions).SingleAsync(trip => trip.Id == cloneResult.ClonedTripId);
        Assert.False(clone.IsPublic);
        Assert.Equal(expected, clone.Regions.OrderBy(region => region.DisplayOrder)
            .Select(region => (region.Name, region.DisplayOrder, region.Notes)));
        Assert.All(clone.Regions, region =>
        {
            Assert.Equal(destination.Id, region.UserId);
            Assert.DoesNotContain(region.Id, imported.Regions.Select(item => item.Id));
        });
    }

    /// <summary>A historical malformed source is rejected before any destination Trip is staged or saved.</summary>
    [PostgresFact]
    public async Task DuplicateCloneSourceCannotCreateDestination()
    {
        var owner = await fixture.CreateUserAsync();
        var destination = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("DROP INDEX \"UX_Regions_Trip_UnassignedPlaces\"");
        var source = new Trip { Id = Guid.NewGuid(), UserId = owner.Id, Name = "Invalid source", IsPublic = true };
        db.Trips.Add(source);
        foreach (var order in new[] { 0, 17 }) source.Regions.Add(new Region
        {
            Id = Guid.NewGuid(), UserId = owner.Id, TripId = source.Id, Name = "Unassigned Places", DisplayOrder = order
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TripCloneCoordinator(db).CloneAsync(source.Id, destination.Id));

        Assert.Contains("reconciliation", failure.Message);
        Assert.Empty(db.ChangeTracker.Entries());
        // Exercise the same subsequent-save hazard as a controller audit, without changing the malformed source.
        db.AuditLogs.Add(new AuditLog { Action = "Error", Details = "Rejected clone", Timestamp = DateTime.UtcNow, UserId = owner.Id });
        await db.SaveChangesAsync();
        await using var verify = fixture.CreateContext();
        Assert.False(await verify.Trips.AnyAsync(trip => trip.UserId == destination.Id));
        Assert.Equal(2, await verify.Regions.CountAsync(region => region.TripId == source.Id));
        await db.Trips.Where(trip => trip.Id == source.Id).ExecuteDeleteAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX "UX_Regions_Trip_UnassignedPlaces" ON "Regions" ("TripId")
            WHERE "Name" = 'Unassigned Places'
            """);
    }

    /// <summary>Creates tiny schema-versioned or generic documents with distinct Region IDs and deliberately preserved orders.</summary>
    private static string Kml(Guid id, int version, params string[] names)
    {
        var native = version == 0 ? "" : $"""
            <ExtendedData><Data name="WayfarerSchemaVersion"><value>{version}</value></Data>
            <Data name="TripId"><value>{id}</value></Data></ExtendedData>
            """;
        var folders = names.Select((name, index) => $"""
            <Folder><name>{name}</name><ExtendedData>
            <Data name="RegionId"><value>{Guid.NewGuid()}</value></Data>
            <Data name="DisplayOrder"><value>{17 + index}</value></Data>
            <Data name="NotesHtml"><value>Region metadata</value></Data>
            </ExtendedData></Folder>
            """);
        return $"""
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document><name>Canonical import</name>
            {native}{string.Join("", folders)}</Document></kml>
            """;
    }
}
