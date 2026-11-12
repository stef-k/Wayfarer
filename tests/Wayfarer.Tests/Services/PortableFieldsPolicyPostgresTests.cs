using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

using Wayfarer.Services;

namespace Wayfarer.Tests.Services;

/// <summary>Batch 1 product evidence at actual persistence, protected mutation, acquisition and import seams.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class PortableFieldsPolicyPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Legacy and explicit view-only refuse real clone/native/Google/PDF before browser work; owners retain access.</summary>
    [PostgresTheory]
    [InlineData(null)]
    [InlineData("view-only")]
    [InlineData("CC-BY-4.0")]
    [InlineData("CC0-1.0")]
    public async Task EveryAcquisitionUsesCurrentPolicy(string? policy)
    {
        var source = await Seed(policy);
        var caller = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var exports = Exporter(db);
        var granted = policy is "CC-BY-4.0" or "CC0-1.0";
        var clone = await new TripCloneCoordinator(db).CloneAsync(source.Id, caller.Id);
        Assert.Equal(granted ? TripCloneStatus.Succeeded : TripCloneStatus.NotPublic, clone.Status);
        if (clone.ClonedTripId is { } id)
        {
            fixture.RegisterTrip(id);
            var copied = await db.Trips.AsNoTracking().Include(t => t.Segments).SingleAsync(t => t.Id == id);
            Assert.False(copied.IsPublic); Assert.Null(copied.ContributionPolicy); Assert.Null(copied.Planning);
            Assert.Equal("Morning walk", copied.Segments.Single().Title);
            Assert.Contains(copied.Credits, c => c.Licence == policy && c.SourceTitle == source.Name);
        }
        if (granted)
        {
            Assert.Equal(5, Parse(exports.GenerateWayfarerKml(source.Id)).Document!.Version);
            var google = exports.GenerateGoogleMyMapsKml(source.Id);
            Assert.Equal(WayfarerKmlKind.Generic, Parse(google).Kind);
            var imported = GoogleMyMapsKmlParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(google)), caller.Id);
            Assert.Contains(imported.Credits, c => c.Notices == "Retain notice");
        }
        else
        {
            Assert.Throws<UnauthorizedAccessException>(() => exports.GenerateWayfarerKml(source.Id));
            Assert.Throws<UnauthorizedAccessException>(() => exports.GenerateGoogleMyMapsKml(source.Id));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => exports.GeneratePdfGuideAsync(source.Id));
        }
        var ownerExports = Exporter(db, source.UserId);
        Assert.Contains("WayfarerSchemaVersion", ownerExports.GenerateWayfarerKml(source.Id));
        // HttpContextAccessor is an AsyncLocal request owner; switch back to a foreign/anonymous request.
        new HttpContextAccessor().HttpContext = new DefaultHttpContext();
        // Already loaded/tracked state cannot authorize a later revoked delivery/acquisition.
        await db.Trips.Where(t => t.Id == source.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ContributionPolicy, "view-only"));
        Assert.Throws<UnauthorizedAccessException>(() => exports.GenerateGoogleMyMapsKml(source.Id));
        Assert.Throws<UnauthorizedAccessException>(() => exports.GenerateWayfarerKml(source.Id));
        await TripReusePolicy.RequireAsync(db, source.Id, source.UserId);
    }

    /// <summary>Selected fields compare their own exact originals and commit atomically, without overwriting disjoint Notes.</summary>
    [PostgresFact]
    public async Task ProtectedTitleAndCreditsPreserveDisjointFieldsAndRejectStaleGroups()
    {
        var trip = await Seed(null);
        var segment = trip.Segments.Single();
        await using var db = fixture.CreateContext();
        var mutation = new TripMetadataMutationService(db);
        await mutation.ApplyAsync(trip.Id, trip.UserId, Json(new { expected = new { notes = (string?)null }, changes = new { notes = "New notes" } }), false, null, default);
        await mutation.ApplyAsync(trip.Id, trip.UserId, Json(new { expected = new { contributionPolicy = (string?)null, credits = trip.Credits },
            changes = new { contributionPolicy = "CC0-1.0", credits = new[] { trip.Credits.Single() with { Modifications = "Adapted" } } } }), false, null, default);
        var stale = await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => mutation.ApplyAsync(trip.Id, trip.UserId,
            Json(new { expected = new { contributionPolicy = (string?)null, name = trip.Name }, changes = new { contributionPolicy = "view-only", name = "Must not commit" } }), false, null, default));
        Assert.Contains("changed", stale.Message);
        var segments = new SegmentNotesConditionalMutationService(db);
        await segments.ApplyAsync(trip.Id, segment.Id, trip.UserId, Json(new { expected = new { title = segment.Title }, changes = new { title = "New title" } }), false, null, default);
        await segments.ApplyAsync(trip.Id, segment.Id, trip.UserId, Json(new { expected = new { notes = (string?)null }, changes = new { notes = "Independent segment notes" } }), false, null, default);
        await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => segments.ApplyAsync(trip.Id, segment.Id, trip.UserId,
            Json(new { expected = new { title = segment.Title, notes = "Independent segment notes" }, changes = new { title = "Stale", notes = "Must not commit" } }), false, null, default));
        var saved = await db.Trips.AsNoTracking().Include(t => t.Segments).SingleAsync(t => t.Id == trip.Id);
        Assert.Equal(trip.Name, saved.Name); Assert.Equal("New notes", saved.Notes); Assert.Equal("CC0-1.0", saved.ContributionPolicy);
        Assert.Equal("Adapted", saved.Credits.Single().Modifications);
        Assert.Equal("New title", saved.Segments.Single().Title);
        Assert.Equal("Independent segment notes", saved.Segments.Single().Notes);
        await segments.ApplyAsync(trip.Id, segment.Id, trip.UserId, Json(new { expected = new { title = "New title" }, changes = new { title = (string?)null } }), false, null, default);
        Assert.Null(await db.Segments.Where(s => s.Id == segment.Id).Select(s => s.Title).SingleAsync());
    }

    /// <summary>Native creates remap identities; older omission retains new fields; reviewed explicit changes and removals require current consent.</summary>
    [PostgresFact]
    public async Task CompleteNativePipelineAndOlderOmission()
    {
        var trip = await Seed("CC0-1.0");
        await using var db = fixture.CreateContext();
        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var xml = TripWayfarerKmlExporter.BuildKml(trip, true);
        var copy = await service.ImportWayfarerKmlAsync(Text(xml), trip.UserId, TripImportMode.CreateNew);
        fixture.RegisterTrip(copy.TripId);
        var copied = await db.Trips.AsNoTracking().Include(t => t.Segments).SingleAsync(t => t.Id == copy.TripId);
        Assert.NotEqual(trip.Id, copied.Id); Assert.False(copied.IsPublic); Assert.Null(copied.ContributionPolicy);
        Assert.Equal(trip.Segments.Single().Title, copied.Segments.Single().Title);
        Assert.Contains(copied.Credits, c => c.Licence == "CC0-1.0");
        var legacy = XDocument.Parse(xml);
        XNamespace k = "http://www.opengis.net/kml/2.2";
        foreach (var data in legacy.Descendants(k + "Data").Where(d => (string?)d.Attribute("name") is "Title" or "Credits" or "ContentReuse").ToArray()) data.Remove();
        legacy.Descendants(k + "Data").Single(d => (string?)d.Attribute("name") == "WayfarerSchemaVersion").Element(k + "value")!.Value = "4";
        var review = await service.ReviewNativeAsync(Text(legacy.ToString()), trip.UserId);
        await service.ImportReviewedNativeAsync(Text(legacy.ToString()), trip.UserId, new(review.Token, []));
        var retained = await db.Trips.AsNoTracking().Include(t => t.Segments).SingleAsync(t => t.Id == trip.Id);
        Assert.Equal(trip.Credits, retained.Credits); Assert.Equal(trip.ContributionPolicy, retained.ContributionPolicy);
        Assert.Equal("Morning walk", retained.Segments.Single().Title);
        trip.Segments.Single().Title = "Imported title";
        var changed = TripWayfarerKmlExporter.BuildKml(trip);
        review = await service.ReviewNativeAsync(Text(changed), trip.UserId);
        var use = review.Planning.Single(r => r.Field == "title").Choices.Single(c => c.Action == "use").Token;
        await service.ImportReviewedNativeAsync(Text(changed), trip.UserId, new(review.Token, [use]));
        Assert.Equal("Imported title", await db.Segments.Where(s => s.TripId == trip.Id).Select(s => s.Title).SingleAsync());
        trip.Segments.Clear();
        var removed = TripWayfarerKmlExporter.BuildKml(trip);
        review = await service.ReviewNativeAsync(Text(removed), trip.UserId);
        await Assert.ThrowsAsync<NativeImportReviewException>(() => service.ImportReviewedNativeAsync(Text(removed), trip.UserId, new(review.Token, [])));
        Assert.Single(await db.Segments.Where(s => s.TripId == trip.Id).ToListAsync());
        var malformed = changed.Replace("name=\"Title\"", "name=\"credits\"");
        await Assert.ThrowsAsync<FormatException>(() => service.ImportWayfarerKmlAsync(Text(malformed), trip.UserId, TripImportMode.CreateNew));
        Assert.Equal(2, await db.Trips.CountAsync(t => t.UserId == trip.UserId));
    }

    /// <summary>The ordinary editor can create an otherwise empty Segment with a title and the legacy explicit null token.</summary>
    [PostgresFact]
    public async Task OrdinaryCreateCarriesTitle()
    {
        var trip = await Seed(null);
        await using var db = fixture.CreateContext();
        var request = Json(new { title = "Morning walk", fromPlaceId = (Guid?)null, toPlaceId = (Guid?)null,
            waypointPlaceIds = Array.Empty<Guid>(), waypointRouteVertexIndices = Array.Empty<int?>(), mode = (string?)null,
            estimatedDistanceKm = (double?)null, estimatedDurationMinutes = 0, estimatedDurationSource = "Manual",
            notesHtml = "", route = (object?)null, aggregateConcurrencyToken = (string?)null });
        using var syntax = await JsonDocument.ParseAsync(request);
        Assert.True(Wayfarer.Models.Dtos.Editor.EditorSegmentRequestParser.TryParseSave(syntax.RootElement, out var parsed, out var errors), JsonSerializer.Serialize(errors));
        Assert.Equal("Morning walk", parsed.TitleIntent!.Desired);
        request.Position = 0;
        var outcome = await new TripEditorSegmentMutationService(db).CreateSegmentAsync(trip.Id, trip.UserId, request, default);
        Assert.True(outcome.Status == EditorRegionMutationStatus.Success, JsonSerializer.Serialize(outcome));
        Assert.Contains(await db.Segments.Where(s => s.TripId == trip.Id).ToListAsync(), s => s.Title == "Morning walk");
    }

    /// <summary>Recognized generic attribution survives private construction; malformed readable/typed claims cannot fall back.</summary>
    [PostgresFact]
    public async Task GoogleAttributionPersistsOrRefusesBeforeCommit()
    {
        var trip = await Seed("CC-BY-4.0");
        await using var db = fixture.CreateContext();
        var google = Exporter(db).GenerateGoogleMyMapsKml(trip.Id);
        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var imported = await service.ImportWayfarerKmlAsync(Text(google), trip.UserId, TripImportMode.CreateNew);
        fixture.RegisterTrip(imported.TripId);
        var saved = await db.Trips.AsNoTracking().SingleAsync(t => t.Id == imported.TripId);
        Assert.False(saved.IsPublic); Assert.Null(saved.ContributionPolicy);
        Assert.Contains(saved.Credits, c => c.Notices == "Retain notice");
        Assert.Contains(saved.Credits, c => c.Licence == "CC-BY-4.0" && c.SourceTitle == trip.Name);
        XNamespace k = "http://www.opengis.net/kml/2.2";
        var changed = XDocument.Parse(google);
        changed.Descendants(k + "description").First().Value = "Stripped notices";
        await Assert.ThrowsAsync<FormatException>(() => service.ImportWayfarerKmlAsync(Text(changed.ToString()), trip.UserId, TripImportMode.CreateNew));
        changed = XDocument.Parse(google);
        var claim = changed.Descendants(k + "Data").Single(d => (string?)d.Attribute("name") == "WayfarerAttribution");
        claim.AddAfterSelf(new XElement(claim));
        await Assert.ThrowsAsync<FormatException>(() => service.ImportWayfarerKmlAsync(Text(changed.ToString()), trip.UserId, TripImportMode.CreateNew));
        Assert.Equal(2, await db.Trips.CountAsync(t => t.UserId == trip.UserId));
    }

    /// <summary>Policy revoked while acquisition is waiting cannot be read from a stale repeatable snapshot into a file.</summary>
    [PostgresFact]
    public async Task AcquisitionWaitCannotCaptureRevokedGrant()
    {
        var trip = await Seed("CC-BY-4.0");
        using var gate = new AcquisitionGate();
        await using var reading = fixture.CreateContext(gate);
        var export = Exporter(reading);
        var attempt = Task.Run(() => Record.Exception(() => export.GenerateWayfarerKml(trip.Id)));
        try
        {
            Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(10)), "The real acquisition did not reach its row barrier.");
            await using var writer = fixture.CreateContext();
            await writer.Trips.Where(t => t.Id == trip.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ContributionPolicy, "view-only"));
        }
        finally { gate.Release.Set(); }
        var refused = await attempt.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(refused is UnauthorizedAccessException or Npgsql.PostgresException { SqlState: "40001" }
            or System.Data.DBConcurrencyException { InnerException: InvalidOperationException { InnerException: Npgsql.PostgresException { SqlState: "40001" } } }, refused?.ToString());
    }

    /// <summary>Pauses only the existing source-row acquisition command; no synthetic writer or alternate import harness.</summary>
    private sealed class AcquisitionGate : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor, IDisposable
    {
        internal readonly ManualResetEventSlim Entered = new();
        internal readonly ManualResetEventSlim Release = new();
        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result)
        {
            if (command.CommandText.Contains("FOR SHARE", StringComparison.Ordinal) && command.CommandText.Contains("Trips", StringComparison.Ordinal))
            {
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Acquisition gate timed out.");
            }
            return result;
        }
        public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
    }

    private async Task<Trip> Seed(string? policy)
    {
        var owner = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = owner.Id, Name = "Portable source", IsPublic = true,
            ContributionPolicy = policy, Credits = [new("Bob", "Unavailable source", "Unfamiliar terms", Notices: "Retain notice")] };
        trip.Segments.Add(new Segment { Id = Guid.NewGuid(), TripId = trip.Id, UserId = owner.Id, Title = "Morning walk" });
        fixture.RegisterTrip(trip.Id);
        await using var db = fixture.CreateContext(); db.Trips.Add(trip); await db.SaveChangesAsync();
        return trip;
    }

    private static TripExportService Exporter(ApplicationDbContext db, string? owner = null) => new(db, null!,
        new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(owner == null ? new ClaimsIdentity()
            : new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner)], "test")) } }, null!, null!,
        NullLogger<TripExportService>.Instance, Mock.Of<IConfiguration>(), null!, Mock.Of<IImageProxyService>());
    private static MemoryStream Text(string text) => new(Encoding.UTF8.GetBytes(text));
    private static MemoryStream Json(object value) => Text(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    private static WayfarerKmlClassification Parse(string xml) => WayfarerKmlParser.ClassifyAndParse(Text(xml));
}
