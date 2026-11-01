using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Planning;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Parsers;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Actual native downloads, complete PostgreSQL fidelity, reviewed originals and transaction rollback.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class NativePlanningPostgresTests(PostgresImportTestFixture fixture) : TestBase
{
    /// <summary>Every reusable field and typed intention survives the actual v4 download/import/owner-read path.</summary>
    [PostgresFact]
    public async Task RichActualDownloadToCreateNewAndOwnerReadPreservesCompleteContent()
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await HostAsync(db);
        using var client = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(client, app.Services, trip.UserId);
        using var exported = await client.GetAsync($"/Trip/ExportPersonalWayfarerKml/{trip.Id}?includePrivatePlanning=true");
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        Assert.True(exported.Headers.CacheControl!.Private && exported.Headers.CacheControl.NoStore);
        Assert.Equal("attachment", exported.Content.Headers.ContentDisposition!.DispositionType);
        var original = await exported.Content.ReadAsStringAsync();
        Assert.Equal(WayfarerKmlKind.NativeV4, WayfarerKmlParser.ClassifyAndParse(File(original)).Kind);
        var destination = await fixture.CreateUserAsync();
        var importer = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var result = await importer.ImportWayfarerKmlAsync(File(original), destination.Id, TripImportMode.CreateNew);
        fixture.RegisterTrip(result.TripId);
        var saved = await NativeTripGraph.Query(db).SingleAsync(t => t.Id == result.TripId);
        Assert.False(saved.IsPublic);
        Assert.Equal(destination.Id, saved.UserId);
        var source = await NativeTripGraph.Query(db).SingleAsync(t => t.Id == trip.Id);
        Assert.Equal(source.Name, saved.Name);
        var originalTargets = NativeTripGraph.Targets(source).ToDictionary(t => (t.Kind, t.Name));
        var idMap = new Dictionary<Guid, Guid>();
        foreach (var target in NativeTripGraph.Targets(saved))
        {
            var before = originalTargets[(target.Kind, target.Name)];
            Assert.NotEqual(before.Id, target.Id);
            idMap.Add(target.Id, before.Id);
            var entries = target.Target.Planning!.Entries;
            Assert.Equal(before.Target.Planning!.Entries.Count, entries.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                Assert.NotEqual(before.Target.Planning.Entries[i].Id, entries[i].Id);
                idMap.Add(entries[i].Id, before.Target.Planning.Entries[i].Id);
                Assert.Equal(PlanningCodec.Write(new(1, [before.Target.Planning.Entries[i]])),
                    PlanningCodec.Write(new(1, [entries[i] with { Id = before.Target.Planning.Entries[i].Id }])));
            }
        }
        var repeated = (await Exporter(db).GeneratePersonalNativeAsync(saved.Id, destination.Id, true)).Kml;
        foreach (var (mapped, old) in idMap) repeated = repeated.Replace(mapped.ToString("D"), old.ToString("D"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Normalize(original).ToString(), Normalize(repeated).ToString());
        db.ApiTokens.Add(new() { User = null!, UserId = destination.Id, Name = "native-owner-read", TokenHash = ApiTokenService.HashToken(destination.Id) });
        await db.SaveChangesAsync();
        using var reader = app.GetTestClient();
        reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", destination.Id);
        using var ownerRead = await reader.GetAsync($"/api/trips/{saved.Id}/planning");
        Assert.Equal(HttpStatusCode.OK, ownerRead.StatusCode);
        var snapshot = await ownerRead.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var target in NativeTripGraph.Targets(saved))
        {
            var planning = snapshot.GetProperty("planning");
            var value = target.Kind == "trip" ? planning.GetProperty("trip") : planning.GetProperty(target.Kind + "s").GetProperty(target.Id.ToString("D"));
            Assert.True(PlanningCodec.Equal(target.Target.Planning, PlanningCodec.Read(value.GetRawText())));
        }
        Assert.Equal(3, snapshot.GetProperty("planning").GetProperty("places").EnumerateObject().Count());
    }

    /// <summary>Omission, equal/absent and differing aggregates use whole-target Keep/Use/Clear choices.</summary>
    [PostgresTheory]
    [InlineData("keep")]
    [InlineData("use")]
    [InlineData("clear")]
    public async Task ReviewedReplacementChoicesAndLegacyOmission(string action)
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var incoming = await NativeTripGraph.Query(db).SingleAsync(t => t.Id == trip.Id);
        var region = incoming.Regions.First();
        var absent = region.Places.Single(p => p.Name == "Via");
        var oldPlan = incoming.Planning;
        await db.Places.Where(p => p.Id == absent.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Planning, (PlanningAggregate?)null));
        incoming.Planning = new(1, [new(Guid.NewGuid(), new("at", new("date", "2028-02-29")), "Replacement")]);
        var xml = TripWayfarerKmlExporter.BuildKml(incoming, true);
        var preview = await service.ReviewNativeAsync(File(xml), trip.UserId);
        Assert.Single(preview.Planning, r => r.State == "conflict");
        Assert.Single(preview.Planning, r => r.State == "absent");
        Assert.All(preview.Planning.Where(r => r.State == "equal"), r => Assert.DoesNotContain(r.Choices, c => c.Action == "use"));
        var root = preview.Planning.Single(r => r.Kind == "trip");
        await service.ImportReviewedNativeAsync(File(xml), trip.UserId, new(preview.Token, [root.Choices.Single(c => c.Action == action).Token]));
        var saved = await NativeTripGraph.Query(db).SingleAsync(t => t.Id == trip.Id);
        Assert.True(PlanningCodec.Equal(action == "clear" ? null : action == "use" ? incoming.Planning : oldPlan, saved.Planning));
        Assert.True(PlanningCodec.Equal(absent.Planning, saved.Regions.SelectMany(r => r.Places).Single(p => p.Id == absent.Id).Planning));
        // v3 has no payload, including at the Trip root. It never becomes an implicit reset.
        var legacy = TripWayfarerKmlExporter.BuildKml(saved);
        var retained = await service.ReviewNativeAsync(File(legacy), trip.UserId);
        await service.ImportReviewedNativeAsync(File(legacy), trip.UserId, new(retained.Token, []));
        var after = await NativeTripGraph.Query(db).SingleAsync(t => t.Id == trip.Id);
        Assert.Equal(TripWayfarerKmlExporter.BuildKml(saved, true).Replace(saved.UpdatedAt.ToString("O"), "TIME"),
            TripWayfarerKmlExporter.BuildKml(after, true).Replace(after.UpdatedAt.ToString("O"), "TIME"));
    }

    /// <summary>Reidentification needs loss consent; stale current material, absence and bytes all fence replay.</summary>
    [PostgresFact]
    public async Task LossConsentAndStaleReviewCannotSilentlyReplacePlanning()
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var incoming = await NativeTripGraph.Query(db).SingleAsync(t => t.Id == trip.Id);
        var area = incoming.Regions.First().Areas.Single();
        var oldId = area.Id; area.Id = Guid.NewGuid();
        var xml = TripWayfarerKmlExporter.BuildKml(incoming, true);
        var review = await service.ReviewNativeAsync(File(xml), trip.UserId);
        var removed = review.Planning.Single(r => r.State == "removed");
        var approval = new NativeImportApproval(review.Token, [removed.Choices.Single().Token]);
        Assert.Equal("import_loss_consent_required", (await Assert.ThrowsAsync<NativeImportReviewException>(() => service.ImportReviewedNativeAsync(File(xml), trip.UserId, new(review.Token, [])))).Code);
        Assert.Equal("import_review_stale", (await Assert.ThrowsAsync<NativeImportReviewException>(() => service.ImportReviewedNativeAsync(File(xml + "\n"), trip.UserId, approval))).Code);
        await db.Trips.Where(t => t.Id == trip.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Notes, "New current Notes"));
        Assert.Equal("import_review_stale", (await Assert.ThrowsAsync<NativeImportReviewException>(() => service.ImportReviewedNativeAsync(File(xml), trip.UserId, approval))).Code);
        review = await service.ReviewNativeAsync(File(xml), trip.UserId);
        approval = new(review.Token, [review.Planning.Single(r => r.State == "removed").Choices.Single().Token]);
        await service.ImportReviewedNativeAsync(File(xml), trip.UserId, approval);
        Assert.False(await db.Areas.AnyAsync(a => a.Id == oldId));
        var installed = await db.Areas.AsNoTracking().SingleAsync(a => a.Id == area.Id);
        Assert.NotEqual(area.Planning!.Entries[0].Id, installed.Planning!.Entries[0].Id);
    }

    /// <summary>A real row-lock wait cannot preserve an obsolete reviewed absence after another writer commits.</summary>
    [PostgresFact]
    public async Task ConcurrentPlanningAdditionAfterReviewIsRevalidatedAfterLockWait()
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        var incoming = await NativeTripGraph.Query(db).SingleAsync(t => t.Id == trip.Id);
        var place = incoming.Regions.First().Places.First();
        await db.Places.Where(p => p.Id == place.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Planning, (PlanningAggregate?)null));
        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var xml = TripWayfarerKmlExporter.BuildKml(incoming, true);
        var review = await service.ReviewNativeAsync(File(xml), trip.UserId);
        await db.Database.OpenConnectionAsync();
        var pid = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        var concurrent = new PlanningAggregate(1, [new(Guid.NewGuid(), new("at", new("date", "2029-01-01")))]);
        await blocker.Places.Where(p => p.Id == place.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Planning, concurrent));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var update = service.ImportReviewedNativeAsync(File(xml), trip.UserId, new(review.Token, []), timeout.Token);
        await using var observer = fixture.CreateConnection(); await observer.OpenAsync();
        var blocked = false;
        for (var i = 0; i < 100 && !blocked; i++)
        {
            await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid = @pid AND wait_event_type = 'Lock')", observer);
            command.Parameters.AddWithValue("pid", pid);
            blocked = (bool)(await command.ExecuteScalarAsync())!;
            if (!blocked) await Task.Delay(20);
        }
        await transaction.CommitAsync();
        var error = await Assert.ThrowsAsync<NativeImportReviewException>(() => update);
        Assert.True(blocked); Assert.Equal("import_review_stale", error.Code);
        Assert.True(PlanningCodec.Equal(concurrent, (await db.Places.AsNoTracking().SingleAsync(p => p.Id == place.Id)).Planning));
        Assert.Equal(trip.Name, (await db.Trips.AsNoTracking().SingleAsync(t => t.Id == trip.Id)).Name);
    }

    /// <summary>Foreign child IDs never hydrate authority or become an upsert adoption path.</summary>
    [PostgresFact]
    public async Task ForeignIdentitiesAndInvalidPlanningRejectBeforeReplacement()
    {
        var trip = await SeedAsync(); var foreign = await SeedAsync();
        await using var db = fixture.CreateContext();
        var incoming = await NativeTripGraph.Query(db).SingleAsync(t => t.Id == trip.Id);
        var area = incoming.Regions.First().Areas.Single();
        area.Id = foreign.Regions.First().Areas.Single().Id;
        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        await Assert.ThrowsAsync<TripImportValidationException>(() => service.ReviewNativeAsync(File(TripWayfarerKmlExporter.BuildKml(incoming, true)), trip.UserId));
        var invalid = TripWayfarerKmlExporter.BuildKml(incoming, true).Replace("2027-03", "2027-13", StringComparison.Ordinal);
        await Assert.ThrowsAsync<FormatException>(() => service.ImportWayfarerKmlAsync(File(invalid), trip.UserId, TripImportMode.CreateNew));
        Assert.Equal(trip.Name, (await db.Trips.AsNoTracking().SingleAsync(t => t.Id == trip.Id)).Name);
        Assert.Equal(2, await db.Trips.CountAsync(t => t.Id == trip.Id || t.Id == foreign.Id));
    }

    /// <summary>Private owner success/failure and opt-out cannot contaminate public bytes or cache validators.</summary>
    [PostgresFact]
    public async Task PersonalAndPublicDownloadsHaveSeparateAudienceAndCachePolicy()
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext(); await using var app = await HostAsync(db);
        using var publicClient = app.GetTestClient();
        var publicUrl = $"/Trip/ExportWayfarerKml/{trip.Id}";
        using var first = await publicClient.GetAsync(publicUrl);
        var bytes = await first.Content.ReadAsByteArrayAsync();
        Assert.DoesNotContain("Planning", Encoding.UTF8.GetString(bytes));
        using var owner = app.GetTestClient(); UserConnectionTokenHttpTests.Cookie(owner, app.Services, trip.UserId);
        using var excluded = await owner.GetAsync($"/Trip/ExportPersonalWayfarerKml/{trip.Id}");
        Assert.Equal(bytes, await excluded.Content.ReadAsByteArrayAsync());
        using var included = await owner.GetAsync($"/Trip/ExportPersonalWayfarerKml/{trip.Id}?includePrivatePlanning=true");
        Assert.Contains("name=\"Planning\"", await included.Content.ReadAsStringAsync());
        using var unauthorized = await publicClient.GetAsync($"/Trip/ExportPersonalWayfarerKml/{trip.Id}?includePrivatePlanning=true");
        var other = await fixture.CreateUserAsync(); using var foreign = app.GetTestClient(); UserConnectionTokenHttpTests.Cookie(foreign, app.Services, other.Id);
        using var forbidden = await foreign.GetAsync($"/Trip/ExportPersonalWayfarerKml/{trip.Id}?includePrivatePlanning=true");
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        foreach (var response in new[] { included, excluded, unauthorized, forbidden })
        {
            Assert.True(response.Headers.CacheControl!.NoStore && response.Headers.CacheControl.Private);
            Assert.Null(response.Headers.ETag); Assert.Null(response.Content.Headers.LastModified);
        }
        using var last = await publicClient.GetAsync(publicUrl + "?includePrivatePlanning=true");
        Assert.Equal(bytes, await last.Content.ReadAsByteArrayAsync());
        Assert.Equal(first.Headers.CacheControl?.ToString(), last.Headers.CacheControl?.ToString());
        Assert.Equal(first.Headers.ETag, last.Headers.ETag);
    }

    /// <summary>A real post-insert reconciliation failure rolls back all material, planning, tags and exact ticks.</summary>
    [PostgresFact]
    public async Task LateReconciliationRollsBackEntireReviewedReplacement()
    {
        var trip = await SeedAsync();
        await using var read = fixture.CreateContext();
        var before = await Exporter(read).GeneratePersonalNativeAsync(trip.Id, trip.UserId, true);
        var incoming = await NativeTripGraph.Query(read).SingleAsync(t => t.Id == trip.Id);
        incoming.Name = "Must roll back"; incoming.Planning = NativeKmlV4Tests.Plan();
        var slug = $"wb2-rollback-{Guid.NewGuid():N}";
        incoming.Tags = [new Tag { Name = slug, Slug = slug }];
        var xml = TripWayfarerKmlExporter.BuildKml(incoming, true);
        var failure = new FailReconciliation(); await using var db = fixture.CreateContext(failure);
        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var review = await service.ReviewNativeAsync(File(xml), trip.UserId);
        var use = review.Planning.Single(r => r.Kind == "trip").Choices.Single(c => c.Action == "use").Token;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportReviewedNativeAsync(File(xml), trip.UserId, new(review.Token, [use])));
        Assert.True(failure.Rebuilt); Assert.Empty(db.ChangeTracker.Entries());
        await using var verify = fixture.CreateContext();
        Assert.Equal(before.Kml, (await Exporter(verify).GeneratePersonalNativeAsync(trip.Id, trip.UserId, true)).Kml);
        Assert.False(await verify.Tags.AnyAsync(t => t.Slug == slug));
    }

    /// <summary>Rich source owns every geometry/reference/provenance seam and all five planning aggregates.</summary>
    private async Task<Trip> SeedAsync()
    {
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Native rich Trip", Notes = "<p>Trip &amp; Notes</p>", IsPublic = true,
            CoverImageUrl = "https://example.invalid/cover.png", CenterLat = 37, CenterLon = 23, Zoom = 9, Planning = NativeKmlV4Tests.Plan() };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, UserId = user.Id, Name = "Region", DisplayOrder = 3, Notes = "<p>Region</p>", Center = new Point(23, 37) { SRID = 4326 }, Planning = NativeKmlV4Tests.Plan() };
        var places = new[] { "From", "Via", "To" }.Select((name, i) => new Place { Id = Guid.NewGuid(), RegionId = region.Id, UserId = user.Id, Name = name,
            DisplayOrder = i == 0 ? null : i * 3, Notes = $"<p>{name}</p>", IconName = "marker", MarkerColor = "blue", Address = $"Address {i}", Location = new Point(23 + i * 0.01, 37 + i * 0.01) { SRID = 4326 },
            ResolvedFeatureName = $"Feature {i}", ResolvedFeatureType = "amenity", AddressEnrichmentProvider = "geoapify", AddressEnrichmentStorageMode = "persistent",
            AddressEnrichedAt = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero), Planning = NativeKmlV4Tests.Plan() }).ToArray();
        region.Places = places;
        region.Areas.Add(new Area { Id = Guid.NewGuid(), RegionId = region.Id, Name = "Holed Area", DisplayOrder = 5, FillHex = "#123456", Notes = "<p>Area</p>", Planning = NativeKmlV4Tests.Plan(),
            Geometry = new Polygon(new LinearRing([new(23, 37), new(24, 37), new(24, 38), new(23, 38), new(23, 37)]), [new LinearRing([new(23.2, 37.2), new(23.4, 37.2), new(23.4, 37.4), new(23.2, 37.2)])]) { SRID = 4326 } });
        trip.Regions.Add(region);
        foreach (var custom in new[] { true, false })
        {
            var segment = new Segment { Id = Guid.NewGuid(), TripId = trip.Id, UserId = user.Id, FromPlaceId = places[0].Id, ToPlaceId = places[2].Id,
                Mode = custom ? "walk" : "", DisplayOrder = custom ? 0 : 4, Notes = "<p>Route</p>", EstimatedDurationSource = custom ? EstimatedDurationSource.Automatic : EstimatedDurationSource.Manual,
                EstimatedDistanceKm = 1.23456789, EstimatedDuration = TimeSpan.FromTicks(1234567890), Planning = NativeKmlV4Tests.Plan(),
                RouteGeometry = custom ? new LineString(places.Select(p => p.Location!.Coordinate).ToArray()) { SRID = 4326 } : null,
                RouteProvider = custom ? "geoapify" : null, RouteStorageMode = custom ? "persistent" : null, RouteAttribution = custom ? "Attribution" : null,
                RouteGeneratedAt = custom ? new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero) : null };
            segment.Waypoints.Add(new() { SegmentId = segment.Id, PlaceId = places[1].Id, Position = 0, RouteVertexIndex = custom ? 1 : null });
            trip.Segments.Add(segment);
        }
        var tag = new Tag { Slug = $"wb2-rich-{Guid.NewGuid():N}", Name = $"WB2 Rich {Guid.NewGuid():N}" };
        trip.Tags.Add(tag); fixture.RegisterTag(tag); fixture.RegisterTrip(trip.Id);
        await using var db = fixture.CreateContext(); db.Trips.Add(trip); await db.SaveChangesAsync(); return trip;
    }

    private Task<Microsoft.AspNetCore.Builder.WebApplication> HostAsync(ApplicationDbContext db) => IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
    {
        services.AddScoped(_ => fixture.CreateContext());
        services.AddScoped<ITripExportService>(s => Exporter(s.GetRequiredService<ApplicationDbContext>()));
        services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new Factory(fixture));
        services.AddSingleton(Mock.Of<IIconColorProvider>()); services.AddSingleton(Mock.Of<ICacheWarmupScheduler>()); services.AddSingleton(Mock.Of<ITripMapThumbnailGenerator>());
    });
    private sealed class Factory(PostgresImportTestFixture fixture) : IDbContextFactory<ApplicationDbContext> { public ApplicationDbContext CreateDbContext() => fixture.CreateContext(); }
    private static TripExportService Exporter(ApplicationDbContext db) => new(db, null!, null!, null!, null!, NullLogger<TripExportService>.Instance, new ConfigurationBuilder().Build(), null!, null!);
    private static MemoryStream File(string text) => new(Encoding.UTF8.GetBytes(text));

    /// <summary>Only server write timestamps are excluded from the full identity-mapped XML content comparison.</summary>
    private static XDocument Normalize(string text)
    {
        var xml = XDocument.Parse(text); XNamespace k = "http://www.opengis.net/kml/2.2";
        foreach (var value in xml.Descendants(k + "Data").Where(d => (string?)d.Attribute("name") == "UpdatedAt")) value.Element(k + "value")!.Value = "TIME";
        return xml;
    }

    private sealed class FailReconciliation : DbCommandInterceptor
    {
        public bool Rebuilt { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO \"Places\"", StringComparison.Ordinal)) Rebuilt = true;
            if (Rebuilt && command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) && command.CommandText.Contains("\"Segments\"", StringComparison.Ordinal)) throw new InvalidOperationException("Induced late reconciliation failure");
            return ValueTask.FromResult(result);
        }
    }
}
