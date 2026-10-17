using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

/// <summary>Characterizes #886 at the real proposal-save, PostgreSQL and routed export boundaries.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class TripSegmentMeasurementExportPostgresTests(
    PostgresImportTestFixture fixture, ITestOutputHelper output) : TestBase
{
    /// <summary>Valid provider estimates survive persistence but violate the native exporter's different measurement policy.</summary>
    [PostgresTheory]
    [InlineData(false, "Segment distance is not canonical.")]
    [InlineData(true, "Segment duration must use whole seconds.")]
    public async Task ProviderSave_PersistsEstimatesRejectedByOwnerAndPublicNativeExport(
        bool fractionalDuration, string expectedFailure)
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync(customRoute: false);
        var protection = new EphemeralDataProtectionProvider();
        var credentials = CredentialTestFactory.Create(protection);
        await using (var authority = fixture.CreateContext())
        {
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
                new MeasurementRouteClient(fractionalDuration), new ProviderRouteGeometryValidator(), contexts,
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
        Assert.Equal(expectedFailure, Assert.Throws<InvalidOperationException>(() => exporter.GenerateWayfarerKml(seed.TripId)).Message);

        await using var app = await StartHostAsync(read);
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://wayfarer.test");
        Authenticate(client, app.Services, seed.UserId);
        await AssertNativeFailureAndGoogleSuccessAsync(client, seed.TripId);
        await read.Trips.Where(item => item.Id == seed.TripId)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.IsPublic, true));
        client.DefaultRequestHeaders.Remove("Cookie");
        await AssertNativeFailureAndGoogleSuccessAsync(client, seed.TripId);
    }

    /// <summary>Ordinary manual edits normalize fractional minutes before persistence and retain native round-trip compatibility.</summary>
    [PostgresFact]
    public async Task ManualEditorSave_NormalizesSecondsAndExportsImportsReexports()
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync(includeSegment: false);
        await using (var write = fixture.CreateContext())
        {
            using var baselineBody = TripEditorSegmentMutationPostgresTestSupport.Body(seed,
                [seed.FirstWaypointId, seed.SecondWaypointId], [1, 2], null, customRoute: true);
            var request = (await JsonNode.ParseAsync(baselineBody))!;
            request["estimatedDurationMinutes"] = 1.5125;
            using var body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(request));
            var saved = await support.Service(write).CreateSegmentAsync(seed.TripId, seed.UserId, body, CancellationToken.None);
            Assert.Equal(EditorRegionMutationStatus.Success, saved.Status);
        }
        string original;
        await using (var read = fixture.CreateContext())
        {
            var stored = await read.Segments.AsNoTracking().SingleAsync(item => item.TripId == seed.TripId);
            Assert.Equal(TimeSpan.FromSeconds(91), stored.EstimatedDuration);
            Assert.Equal(SegmentMeasurementCalculator.CalculateDistance(stored.RouteGeometry!.Coordinates).RoundedKilometres,
                stored.EstimatedDistanceKm);
            original = Exporter(read).GenerateWayfarerKml(seed.TripId);
        }
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
        var scheme = IdentityConstants.ApplicationScheme;
        var options = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(scheme);
        var ticket = new AuthenticationTicket(CreateUserPrincipal(userId), new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10)
        }, scheme);
        client.DefaultRequestHeaders.Add("Cookie", options.Cookie.Name + "=" + options.TicketDataFormat.Protect(ticket));
    }

    /// <summary>Observes the known native failure separately from successful Google presentation output.</summary>
    private static async Task AssertNativeFailureAndGoogleSuccessAsync(HttpClient client, Guid tripId)
    {
        using var native = await client.GetAsync($"/Trip/ExportWayfarerKml/{tripId:D}");
        Assert.Equal(HttpStatusCode.InternalServerError, native.StatusCode);
        Assert.Null(native.Content.Headers.ContentDisposition);
        Assert.DoesNotContain("Segment distance", await native.Content.ReadAsStringAsync());
        using var google = await client.GetAsync($"/Trip/ExportGoogleMyMapsKml/{tripId:D}");
        Assert.Equal(HttpStatusCode.OK, google.StatusCode);
        XNamespace kml = "http://www.opengis.net/kml/2.2";
        var document = XDocument.Parse(await google.Content.ReadAsStringAsync());
        Assert.NotEmpty(document.Descendants(kml + "LineString"));
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
            is "DistanceKm" or "DurationSeconds" or "DurationSource")
        .Select(item => item.Value).ToArray();

    /// <summary>Controls only the external HTTP boundary; geometry validation and proposal acceptance remain real.</summary>
    private sealed class MeasurementRouteClient(bool fractionalDuration) : IProviderRouteClient
    {
        public Task<ProviderRouteResult> RouteAsync(ResolvedRoutingProviderExecution execution,
            IReadOnlyList<RouteCoordinate> anchors, Func<CancellationToken, Task<bool>> validateAuthority,
            CancellationToken cancellationToken)
        {
            var distance = SegmentMeasurementCalculator.CalculateDistance(
                anchors.Select(item => new Coordinate(item.Longitude, item.Latitude)).ToArray());
            return Task.FromResult(new ProviderRouteResult(true, anchors, anchors, null,
                (distance.RoundedKilometres + (fractionalDuration ? 0 : 0.010123)) * 1000d,
                fractionalDuration ? 360.25 : 360, [], Enumerable.Range(0, anchors.Count).ToArray()));
        }
    }
}
