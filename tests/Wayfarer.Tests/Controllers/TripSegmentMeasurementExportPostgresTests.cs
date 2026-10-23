using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Services;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Controllers;

/// <summary>Proves #886 at the real proposal-save, PostgreSQL and routed native interchange boundaries.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class TripSegmentMeasurementExportPostgresTests(
    PostgresImportTestFixture fixture, ITestOutputHelper output) : TestBase
{
    /// <summary>The accepted provider result survives owner/public export and native import without recalculation.</summary>
    [PostgresTheory]
    [InlineData(false, false, "Segment distance is not canonical.")]
    [InlineData(true, true, "Segment duration must use whole seconds.")]
    [InlineData(true, false, "Automatic duration is not canonical.")]
    public async Task ProviderSave_PreservesAcceptedEstimatesThroughOwnerAndPublicNativeInterchange(
        bool canonicalDistance, bool fractionalDuration, string expectedFailure)
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync(customRoute: false);
        var protection = new EphemeralDataProtectionProvider();
        var credentials = CredentialTestFactory.Create(protection);
        await using (var authority = fixture.CreateContext())
        {
            // Keep the controlled provider estimates representative of a short urban journey.
            var anchorIds = new[] { seed.FromId, seed.FirstWaypointId, seed.SecondWaypointId, seed.ToId };
            var places = await authority.Places.Where(item => anchorIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id);
            for (var index = 0; index < anchorIds.Length; index++)
                places[anchorIds[index]].Location = new Point(23.7 + index * 0.005, 37.9 + index * 0.005) { SRID = 4326 };
            var profile = PersonalLocationProviderProfile.Create(seed.UserId, PersonalLocationProvider.Geoapify);
            credentials.Replace(profile, "synthetic-test-credential");
            profile.SetAuthorization(PersonalProviderCapability.Routing, true);
            credentials.RecordVerification(profile, PersonalProviderCapability.Routing, PersonalProviderVerification.Verified);
            var selection = PersonalLocationProviderSelection.Create(seed.UserId);
            selection.Select(PersonalProviderCapability.Routing, PersonalLocationProvider.Geoapify);
            authority.AddRange(profile, selection);
            await authority.SaveChangesAsync();
        }

        ExternalRouteProposalDto proposal;
        await using (var write = fixture.CreateContext())
        {
            var tokens = new SegmentAggregateTokenService(protection);
            var contexts = new ExternalRouteProposalContextService(protection);
            var resolver = new AuthoritativeRoutingProviderResolver(write, credentials);
            var service = new TripEditorSegmentMutationService(write, tokens,
                new SegmentRouteClearConfirmation(protection, TimeProvider.System),
                new ExternalRouteProposalSaveValidator(write, tokens, contexts, resolver));
            var original = await write.Segments.AsNoTracking().SingleAsync(item => item.Id == seed.SegmentId);
            var token = service.IssueAggregateToken(seed.UserId, seed.TripId, original);
            var generator = new ExternalRouteProposalGenerator(write, tokens,
                new MeasurementRouteClient(canonicalDistance, fractionalDuration), new ProviderRouteGeometryValidator(), contexts,
                new RoutingRequestBudget(), resolver);
            var generated = await generator.GenerateAsync(seed.UserId, seed.TripId, original.Id,
                token, "drive", CancellationToken.None);
            Assert.True(generated.Succeeded, generated.ErrorCode);
            proposal = generated.Proposal!;
            using var body = ProposalBody(seed, token, proposal);
            var saved = await service.UpdateSegmentAsync(seed.TripId, original.Id, seed.UserId,
                body, null, CancellationToken.None);
            Assert.Equal(EditorRegionMutationStatus.Success, saved.Status);
        }

        // A new context proves that PostgreSQL has not introduced the discrepancy.
        await using var read = fixture.CreateContext();
        var stored = await read.Segments.Include(item => item.TransportProfile)
            .Include(item => item.FromPlace).ThenInclude(item => item!.Region)
            .Include(item => item.ToPlace).ThenInclude(item => item!.Region)
            .Include(item => item.Waypoints).ThenInclude(item => item.Place).ThenInclude(item => item.Region)
            .SingleAsync(item => item.Id == seed.SegmentId);
        var canonical = SegmentMeasurementCalculator.CalculateDistance(stored.RouteGeometry!.Coordinates);
        Assert.Equal(proposal.DistanceMetres / 1000d, stored.EstimatedDistanceKm);
        Assert.Equal(TimeSpan.FromSeconds(proposal.DurationSeconds!.Value), stored.EstimatedDuration);
        Assert.Equal(EstimatedDurationSource.Automatic, stored.EstimatedDurationSource);
        Assert.Equal("geoapify", stored.RouteProvider);
        output.WriteLine($"sourceMetres={proposal.DistanceMetres:R}; storedKm={stored.EstimatedDistanceKm:R}; canonicalKm={canonical.RoundedKilometres:R}");
        output.WriteLine($"sourceSeconds={proposal.DurationSeconds:R}; storedTicks={stored.EstimatedDuration!.Value.Ticks}; canonicalSeconds={SegmentMeasurementCalculator.CalculateAutomaticDuration(canonical.UnroundedMetres, stored.TransportProfile!.PlanningSpeedKmh!.Value).TotalSeconds:R}");

        var projection = PublicSegmentResolver.Resolve(stored, seed.TripId, read);
        Assert.True(projection.Succeeded);
        Assert.Equal(stored.EstimatedDistanceKm, projection.Segment!.EstimatedDistanceKm);
        Assert.Equal(stored.EstimatedDuration.Value.TotalMinutes, projection.Segment.EstimatedDurationMinutes);
        var exporter = Exporter(read);
        output.WriteLine($"Former rejection: {expectedFailure}");
        var exported = exporter.GenerateWayfarerKml(seed.TripId);
        await AssertRoundTripAsync(exported, stored);

        await using var app = await StartHostAsync(read);
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://wayfarer.test");
        Authenticate(client, app.Services, seed.UserId);
        await AssertNativeAndGoogleSuccessAsync(client, seed.TripId);
        await read.Trips.Where(item => item.Id == seed.TripId)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.IsPublic, true));
        client.DefaultRequestHeaders.Remove("Cookie");
        await AssertNativeAndGoogleSuccessAsync(client, seed.TripId);
    }

    /// <summary>Wayfarer and Manual saves round-trip their accepted values even if the destination planning speed differs.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WayfarerEditorSave_PreservesAcceptedMeasurementsAcrossDifferentImportPlanningSpeed(bool automatic)
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync(includeSegment: false);
        await using (var write = fixture.CreateContext())
        {
            using var baselineBody = TripEditorSegmentMutationPostgresTestSupport.Body(seed,
                [seed.FirstWaypointId, seed.SecondWaypointId], [1, 2], null, customRoute: true);
            var request = (await JsonNode.ParseAsync(baselineBody))!;
            request["estimatedDurationMinutes"] = 1.5125;
            request["estimatedDurationSource"] = automatic ? "Automatic" : "Manual";
            using var body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(request));
            var saved = await support.Service(write).CreateSegmentAsync(seed.TripId, seed.UserId, body, CancellationToken.None);
            Assert.Equal(EditorRegionMutationStatus.Success, saved.Status);
        }
        string original;
        await using (var read = fixture.CreateContext())
        {
            var stored = await read.Segments.AsNoTracking().SingleAsync(item => item.TripId == seed.TripId);
            var measured = SegmentMeasurementCalculator.CalculateDistance(stored.RouteGeometry!.Coordinates);
            Assert.Equal(automatic ? SegmentMeasurementCalculator.CalculateAutomaticDuration(measured.UnroundedMetres, 5)
                : TimeSpan.FromSeconds(91), stored.EstimatedDuration);
            Assert.Equal(SegmentMeasurementCalculator.CalculateDistance(stored.RouteGeometry!.Coordinates).RoundedKilometres,
                stored.EstimatedDistanceKm);
            original = Exporter(read).GenerateWayfarerKml(seed.TripId);
        }
        // Simulate another installation's profile settings without changing the exported active result.
        await using (var destinationSettings = fixture.CreateContext())
            await destinationSettings.Set<TransportProfile>().Where(item => item.Id == seed.FirstProfileId)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.PlanningSpeedKmh, 20));
        var destination = await fixture.CreateUserAsync();
        Guid imported;
        await using (var write = fixture.CreateContext())
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(original));
            imported = (await new TripImportService(write, NullLogger<TripImportService>.Instance)
                .ImportWayfarerKmlAsync(stream, destination.Id)).TripId;
            fixture.RegisterTrip(imported);
        }
        await using var verify = fixture.CreateContext();
        var reexported = Exporter(verify).GenerateWayfarerKml(imported);
        Assert.Equal(MeasurementData(original), MeasurementData(reexported));
    }

    /// <summary>Invalid v3 measurements or retention provenance cannot partially replace an owned Trip.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeV3_InvalidActiveStateRejectsUpsertWithoutChangingSavedTrip(bool invalidProvenance)
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync();
        await using var read = fixture.CreateContext();
        var original = Exporter(read).GenerateWayfarerKml(seed.TripId);
        var xml = XDocument.Parse(original);
        var data = xml.Descendants().Single(item => item.Name.LocalName == "Data"
            && item.Attribute("name")?.Value == (invalidProvenance ? "RouteProvenance" : "DistanceKm"));
        data.Elements().Single().Value = invalidProvenance
            ? "{\"Provider\":\"google\",\"StorageMode\":\"ephemeral\"}" : "-1";
        await using (var write = fixture.CreateContext())
        {
            using var body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml.ToString()));
            await Assert.ThrowsAsync<TripImportValidationException>(() =>
                new TripImportService(write, NullLogger<TripImportService>.Instance)
                    .ImportWayfarerKmlAsync(body, seed.UserId, TripImportMode.Upsert));
            Assert.Empty(write.ChangeTracker.Entries());
        }
        await using var verify = fixture.CreateContext();
        Assert.Equal(original, Exporter(verify).GenerateWayfarerKml(seed.TripId));
    }

    /// <summary>Valid historical values export and round-trip without invented provider provenance or read-side repair.</summary>
    [PostgresTheory]
    [InlineData(false, "Segment distance is not canonical.")]
    [InlineData(true, "Segment duration must use whole seconds.")]
    public async Task HistoricalMeasurements_PreserveSavedValuesThroughNativeInterchange(
        bool fractionalDuration, string expectedFailure)
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync();
        double distance;
        var duration = fractionalDuration ? TimeSpan.FromMinutes(1.5125) : TimeSpan.FromMinutes(90);
        await using (var write = fixture.CreateContext())
        {
            var segment = await write.Segments.SingleAsync(item => item.Id == seed.SegmentId);
            distance = SegmentMeasurementCalculator.CalculateDistance(segment.RouteGeometry!.Coordinates).RoundedKilometres
                + (fractionalDuration ? 0 : 0.010123);
            segment.EstimatedDistanceKm = distance;
            segment.EstimatedDuration = duration;
            await write.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext();
        var stored = await read.Segments.AsNoTracking().SingleAsync(item => item.Id == seed.SegmentId);
        Assert.Equal(distance, stored.EstimatedDistanceKm);
        Assert.Equal(duration.Ticks, stored.EstimatedDuration!.Value.Ticks);
        Assert.Null(stored.RouteProvider);
        Assert.Equal(EstimatedDurationSource.Manual, stored.EstimatedDurationSource);
        var exported = Exporter(read).GenerateWayfarerKml(seed.TripId);
        await AssertRoundTripAsync(exported, stored);
        output.WriteLine($"historical storedKm={distance:R}; storedTicks={duration.Ticks}; former rejection={expectedFailure}");
    }

    /// <summary>Uses production Identity, MVC routing and the established safe server-error page.</summary>
    private Task<Microsoft.AspNetCore.Builder.WebApplication> StartHostAsync(ApplicationDbContext db) =>
        IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped<ITripExportService>(_ => Exporter(db));
            var settings = new Mock<IApplicationSettingsService>();
            settings.Setup(service => service.GetSettings()).Returns(new ApplicationSettings());
            services.AddSingleton(settings.Object);
        }, app =>
        {
            app.UseExceptionHandler("/Home/Error");
            app.MapControllerRoute("export-error", "Home/Error", new { controller = "Home", action = "Error" });
        });

    /// <summary>Creates only the KML service dependencies used by the production export routes.</summary>
    private static TripExportService Exporter(ApplicationDbContext db) => new(db, null!, null!, null!, null!,
        NullLogger<TripExportService>.Instance, new ConfigurationBuilder().Build(), null!, null!);

    /// <summary>Protects an owner cookie with the test host's actual Identity scheme.</summary>
    private static void Authenticate(HttpClient client, IServiceProvider services, string userId)
    {
        client.DefaultRequestHeaders.Add("Cookie", IdentityRouteHost.Cookie(services, userId, "User"));
    }

    /// <summary>Both routed exports accept the saved estimates while retaining their separate format contracts.</summary>
    private static async Task AssertNativeAndGoogleSuccessAsync(HttpClient client, Guid tripId)
    {
        using var native = await client.GetAsync($"/Trip/ExportWayfarerKml/{tripId:D}");
        Assert.Equal(HttpStatusCode.OK, native.StatusCode);
        Assert.NotNull(native.Content.Headers.ContentDisposition);
        XDocument.Parse(await native.Content.ReadAsStringAsync());
        using var google = await client.GetAsync($"/Trip/ExportGoogleMyMapsKml/{tripId:D}");
        Assert.Equal(HttpStatusCode.OK, google.StatusCode);
        XNamespace kml = "http://www.opengis.net/kml/2.2";
        var document = XDocument.Parse(await google.Content.ReadAsStringAsync());
        Assert.NotEmpty(document.Descendants(kml + "LineString"));
        Assert.Contains(document.Descendants(kml + "description"), item =>
            item.Value.StartsWith("00:06 h by ", StringComparison.Ordinal));
    }

    /// <summary>Uses a different owner and fresh contexts to prove exact active values and provenance survive import/re-export.</summary>
    private async Task AssertRoundTripAsync(string exported, Segment original)
    {
        var destination = await fixture.CreateUserAsync();
        Guid imported;
        await using (var write = fixture.CreateContext())
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(exported));
            imported = (await new TripImportService(write, NullLogger<TripImportService>.Instance)
                .ImportWayfarerKmlAsync(stream, destination.Id)).TripId;
            fixture.RegisterTrip(imported);
        }
        await using var verify = fixture.CreateContext();
        var saved = await verify.Segments.AsNoTracking().SingleAsync(item => item.TripId == imported);
        Assert.Equal(original.EstimatedDistanceKm, saved.EstimatedDistanceKm);
        Assert.Equal(original.EstimatedDuration, saved.EstimatedDuration);
        Assert.Equal(original.EstimatedDurationSource, saved.EstimatedDurationSource);
        Assert.Equal(original.RouteProvider, saved.RouteProvider);
        Assert.Equal(original.RouteMappingMode, saved.RouteMappingMode);
        Assert.Equal(original.RouteStorageMode, saved.RouteStorageMode);
        Assert.Equal(original.RouteAttribution, saved.RouteAttribution);
        Assert.Equal(original.RouteGeneratedAt, saved.RouteGeneratedAt);
        Assert.Equal(original.RouteInstructionsJson, saved.RouteInstructionsJson);
        Assert.Equal(MeasurementData(exported), MeasurementData(Exporter(verify).GenerateWayfarerKml(imported)));
    }

    /// <summary>Submits the protected proposal through the ordinary complete editor JSON request.</summary>
    private static Stream ProposalBody(SegmentSeed seed, string token, ExternalRouteProposalDto proposal) =>
        new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
        {
            fromPlaceId = seed.FromId, toPlaceId = seed.ToId,
            waypointPlaceIds = new[] { seed.FirstWaypointId, seed.SecondWaypointId },
            waypointRouteVertexIndices = proposal.WaypointIndices.Skip(1).SkipLast(1), mode = seed.FirstProfileKey,
            estimatedDistanceKm = 999, estimatedDurationMinutes = 999, estimatedDurationSource = "Automatic", notesHtml = "proposal",
            route = new { type = "LineString", coordinates = proposal.Geometry.Select(item => new[] { item.Longitude, item.Latitude }) },
            aggregateConcurrencyToken = token,
            proposal = new { proposal.ProposalId, proposal.ProtectedContext, manualDurationOverride = false }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    /// <summary>Compares the native measurement metadata independently of remapped identities.</summary>
    private static string[] MeasurementData(string xml) => XDocument.Parse(xml).Descendants()
        .Where(item => item.Name.LocalName == "Data" && item.Attribute("name")?.Value
            is "DistanceKm" or "DurationSeconds" or "DurationTicks" or "DurationSource")
        .Select(item => item.Value).ToArray();

    /// <summary>Controls only the external HTTP boundary; geometry validation and proposal acceptance remain real.</summary>
    private sealed class MeasurementRouteClient(bool canonicalDistance, bool fractionalDuration) : IProviderRouteClient
    {
        public Task<ProviderRouteResult> RouteAsync(ResolvedRoutingProviderExecution execution,
            IReadOnlyList<RouteCoordinate> anchors, Func<CancellationToken, Task<bool>> validateAuthority,
            CancellationToken cancellationToken)
        {
            var distance = SegmentMeasurementCalculator.CalculateDistance(
                anchors.Select(item => new Coordinate(item.Longitude, item.Latitude)).ToArray());
            return Task.FromResult(new ProviderRouteResult(true, anchors, anchors, null,
                (distance.RoundedKilometres + (canonicalDistance ? 0 : 0.010123)) * 1000d,
                fractionalDuration ? 360.25 : 360, [], Enumerable.Range(0, anchors.Count).ToArray()));
        }
    }
}
