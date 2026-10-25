using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using System.Text.Json;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves the focused personal-provider routing boundaries retained by issue 538 step 3.</summary>
public sealed class PersonalRoutingBoundaryRemediationTests : TestBase
{
    /// <summary>Publishes spans against the returned geometry, preserving leg and instruction boundary positions.</summary>
    [Theory]
    [InlineData(5)]
    [InlineData(601)]
    public async Task MobileRoute_PreservesInstructionBoundariesAfterBudgeting(int pointsPerLeg)
    {
        var fixture = CreateFixture();
        var route = await InstructedRouteAsync(fixture.Anchors, pointsPerLeg);
        var client = new CallbackRouteClient(route: route);
        var service = new MobileRoutingService(fixture.Db, fixture.Resolver, client,
            new ProviderRouteGeometryValidator(), new RoutingRequestBudget(),
            new MobileRoutingProfileDiscoveryService(fixture.Db, fixture.Credentials));

        var result = await service.RouteAsync(fixture.UserId, fixture.PlanningProfile.Id, fixture.Anchors,
            "drive", null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Outcome);
        Assert.Equal(1, client.Requests);
        Assert.Equal(route.Instructions.Count, result.Instructions!.Count);
        if (pointsPerLeg == 5)
        {
            Assert.Equal(route.Geometry, result.Geometry);
            Assert.Equal(route.Instructions, result.Instructions);
        }
        else Assert.True(result.Geometry!.Count < route.Geometry.Count);
        for (var index = 0; index < route.Instructions.Count; index++)
        {
            var original = route.Instructions[index];
            var final = result.Instructions[index];
            Assert.InRange(final.FromIndex, 0, result.Geometry!.Count - 1);
            Assert.InRange(final.ToIndex, final.FromIndex, result.Geometry.Count - 1);
            if (index > 0) Assert.True(final.FromIndex >= result.Instructions[index - 1].ToIndex);
            Assert.Equal(route.Geometry[original.FromIndex], result.Geometry[final.FromIndex]);
            Assert.Equal(route.Geometry[original.ToIndex], result.Geometry[final.ToIndex]);
            Assert.Equal(original, final with { FromIndex = original.FromIndex, ToIndex = original.ToIndex });
        }
        Assert.Equal(fixture.Anchors[0], result.Geometry![0]);
        Assert.Equal(fixture.Anchors[^1], result.Geometry[^1]);
        Assert.Equal(route.DistanceMetres, result.DistanceMetres);
        Assert.Equal(route.DurationSeconds, result.DurationSeconds);
        Assert.Equal("persistent", result.StorageMode);
        Assert.Equal("geoapify", result.Provider);
        Assert.Equal(2, result.Attribution!.Count);
    }

    /// <summary>Parses a local two-leg provider payload, including a repeated boundary vertex and point instruction.</summary>
    private static async Task<ProviderRouteResult> InstructedRouteAsync(IReadOnlyList<RouteCoordinate> anchors, int pointsPerLeg)
    {
        var lines = anchors.Zip(anchors.Skip(1), (start, end) =>
            Enumerable.Range(0, pointsPerLeg).Select(index => index == pointsPerLeg - 1 ? end : new RouteCoordinate(
                start.Longitude + (end.Longitude - start.Longitude) * index / (pointsPerLeg - 1),
                start.Latitude + (end.Latitude - start.Latitude) * index / (pointsPerLeg - 1))).ToList()).ToArray();
        lines[0].Insert(pointsPerLeg - 1, lines[0][pointsPerLeg - 2]);
        var json = JsonSerializer.Serialize(new
        {
            results = new[] { new
            {
                distance = 1250, time = 360,
                geometry = lines.Select(line => line.Select(point => new { lon = point.Longitude, lat = point.Latitude })),
                legs = lines.Select(line => new
                {
                    distance = 625, time = 180,
                    steps = new[] { (0, 2), (2, line.Count - 2), (line.Count - 2, line.Count - 1), (line.Count - 1, line.Count - 1) }
                        .Select(span => new { from_index = span.Item1, to_index = span.Item2, distance = 0, time = 0,
                            instruction = new { text = "Continue", type = "Straight" } })
                })
            } }
        });
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) };
        var route = await GeoapifyRoutingAdapter.ParseAsync(response, anchors);
        Assert.True(route.Succeeded, route.ErrorCode);
        return route;
    }

    /// <summary>Planning identity is retained context, never permission to request an explicit native mode.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generator_PreservesInactiveOrMissingChoiceAndUsesExplicitMode(bool missingIdentity)
    {
        var fixture = CreateFixture();
        fixture.PlanningProfile.IsActive = false;
        fixture.PlanningProfile.Key = "fish";
        fixture.Segment.Mode = "Fish";
        if (missingIdentity) fixture.Segment.TransportProfileId = null;
        await fixture.Db.SaveChangesAsync();
        var identity = fixture.Segment.TransportProfileId;
        var capability = await new ExternalRoutingCapabilityProjector(fixture.Resolver)
            .ProjectAsync(fixture.UserId, [fixture.Segment], default);
        Assert.True(capability[fixture.Segment.Id].Available);
        var client = new CallbackRouteClient();
        var generator = new ExternalRouteProposalGenerator(fixture.Db, fixture.AggregateTokens, client,
            new AcceptingGeometryValidator(), fixture.ProposalContexts, new RoutingRequestBudget(), fixture.Resolver);
        var invalid = await generator.GenerateAsync(fixture.UserId, fixture.Trip.Id, fixture.Segment.Id,
            fixture.AggregateToken, "Fish", default);
        Assert.Equal("unsupported-provider-mode", invalid.ErrorCode);
        Assert.Equal(0, client.Requests);
        var result = await generator.GenerateAsync(fixture.UserId, fixture.Trip.Id, fixture.Segment.Id,
            fixture.AggregateToken, "drive", default);
        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.Equal("drive", client.Mode);
        Assert.True(fixture.ProposalContexts.TryRead(result.Proposal!.ProtectedContext, out var binding));
        Assert.Equal(identity, binding!.TransportProfileId);
        Assert.Equal("Fish", fixture.Segment.Mode);
        Assert.Equal(identity, fixture.Segment.TransportProfileId);
        Assert.Null(fixture.Segment.RouteGeometry);
        var request = new EditorSegmentSaveRequest(fixture.From.Id, fixture.To.Id, [fixture.Via.Id], [1],
            "Fish", null, null, EstimatedDurationSource.Automatic, null,
            new LineString(result.Proposal.Geometry.Select(item => new Coordinate(item.Longitude, item.Latitude)).ToArray()) { SRID = 4326 },
            fixture.AggregateToken, new(result.Proposal.ProposalId, result.Proposal.ProtectedContext));
        var validator = new ExternalRouteProposalSaveValidator(fixture.Db, fixture.AggregateTokens, fixture.ProposalContexts, fixture.Resolver);
        Assert.Null(await validator.ValidateFinalAsync(binding, fixture.Segment, request, identity, default));
        if (missingIdentity)
            Assert.Equal("route-proposal-stale", await validator.ValidateFinalAsync(binding, fixture.Segment, request with { Mode = "" }, null, default));
        Assert.Equal("route-proposal-stale", await validator.ValidateFinalAsync(binding, fixture.Segment, request, Guid.NewGuid(), default));
        fixture.Segment.TransportProfileId = Guid.NewGuid();
        Assert.Equal("route-proposal-stale", await validator.ValidateFinalAsync(binding, fixture.Segment, request, identity, default));
    }

    [Fact]
    public async Task Generator_RejectsCredentialReplacementAfterContactWithoutPublishingProposal()
    {
        var fixture = CreateFixture();
        var client = new CallbackRouteClient(async token =>
        {
            fixture.Credentials.Replace(fixture.ProviderProfile, "replacement");
            await fixture.Db.SaveChangesAsync(token);
        });
        var service = new ExternalRouteProposalGenerator(fixture.Db, fixture.AggregateTokens, client,
            new AcceptingGeometryValidator(), fixture.ProposalContexts, new RoutingRequestBudget(), fixture.Resolver);

        var result = await service.GenerateAsync(fixture.UserId, fixture.Trip.Id, fixture.Segment.Id,
            fixture.AggregateToken, "drive", CancellationToken.None);

        Assert.Equal(1, client.Requests);
        Assert.False(result.Succeeded);
        Assert.Equal("route-proposal-context-stale", result.ErrorCode);
        Assert.Null(result.Proposal);
        Assert.Null(fixture.Segment.RouteGeometry);
        Assert.Equal("replacement", fixture.Credentials.Read(fixture.ProviderProfile).Credential);
    }

    [Fact]
    public async Task MobileRoute_RejectsSelectionChangeBeforePublicationWithoutModeSubstitution()
    {
        var fixture = CreateFixture();
        var client = new CallbackRouteClient();
        var discovery = new MobileRoutingProfileDiscoveryService(fixture.Db, fixture.Credentials);
        var service = new MobileRoutingService(fixture.Db, fixture.Resolver, client,
            new AcceptingGeometryValidator(), new RoutingRequestBudget(), discovery)
        {
            BeforeRoutePublicationAsync = async token =>
            {
                fixture.Selection.Select(PersonalProviderCapability.Routing, null);
                await fixture.Db.SaveChangesAsync(token);
            }
        };
        var capability = await service.CapabilityAsync(fixture.UserId, fixture.PlanningProfile.Id,
            "drive", null, CancellationToken.None);

        var result = await service.RouteAsync(fixture.UserId, fixture.PlanningProfile.Id, fixture.Anchors,
            "drive", capability.SelectedProfileAuthorityIdentity, CancellationToken.None);

        Assert.Equal(1, client.Requests);
        Assert.False(result.Succeeded);
        Assert.Equal("authority-changed", result.Outcome);
        Assert.Null(result.Geometry);
        Assert.Null(result.ProviderMode);
        Assert.Null(fixture.Selection.RoutingProviderKey);
    }

    /// <summary>The Save validator rejects altered final drafts and rechecks expiry without mutating the aggregate.</summary>
    [Theory]
    [InlineData("anchors")]
    [InlineData("geometry")]
    [InlineData("indices")]
    [InlineData("profile")]
    [InlineData("expiry")]
    public async Task SaveValidation_RejectsAlteredFinalDraftOrOriginalExpiry(string defect)
    {
        var fixture = CreateFixture();
        var clock = new ProposalClock();
        var contexts = new ExternalRouteProposalContextService(new EphemeralDataProtectionProvider(), clock);
        var generator = new ExternalRouteProposalGenerator(fixture.Db, fixture.AggregateTokens, new CallbackRouteClient(),
            new AcceptingGeometryValidator(), contexts, new RoutingRequestBudget(), fixture.Resolver);
        var generated = await generator.GenerateAsync(fixture.UserId, fixture.Trip.Id, fixture.Segment.Id,
            fixture.AggregateToken, "drive", CancellationToken.None);
        Assert.True(generated.Succeeded);
        var proposal = generated.Proposal!;
        var request = new EditorSegmentSaveRequest(fixture.From.Id, fixture.To.Id, [fixture.Via.Id], [1],
            fixture.PlanningProfile.Key, null, null, EstimatedDurationSource.Automatic, "changed notes",
            new LineString(proposal.Geometry.Select(item => new Coordinate(item.Longitude, item.Latitude)).ToArray()) { SRID = 4326 },
            fixture.AggregateToken, new(proposal.ProposalId, proposal.ProtectedContext));
        if (defect == "anchors") request = request with { FromPlaceId = fixture.To.Id };
        if (defect == "geometry") request = request with { Route = new LineString([new(1, 1), new(2, 2), new(3, 3)]) { SRID = 4326 } };
        if (defect == "indices") request = request with { WaypointRouteVertexIndices = [0] };
        var validator = new ExternalRouteProposalSaveValidator(fixture.Db, fixture.AggregateTokens, contexts, fixture.Resolver);
        var locked = await validator.LockAuthorityAsync(fixture.UserId, fixture.Trip.Id, fixture.Segment.Id, request, CancellationToken.None);
        if (defect == "expiry") clock.Advance(TimeSpan.FromMinutes(10));
        var error = locked.Error ?? await validator.ValidateFinalAsync(locked.Binding!, fixture.Segment, request,
            defect == "profile" ? Guid.NewGuid() : fixture.PlanningProfile.Id, CancellationToken.None);
        Assert.NotNull(error);
        Assert.Null(fixture.Segment.RouteGeometry);
        Assert.Null(fixture.Segment.RouteProvider);
        Assert.NotEqual("changed notes", fixture.Segment.Notes);
        if (defect == "expiry") Assert.Equal("route-proposal-invalid-or-expired", error);
    }

    /// <summary>Advances only the proposal lifetime clock, without provider calls or wall-clock waits.</summary>
    private sealed class ProposalClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private Fixture CreateFixture()
    {
        const string userId = "personal-routing-owner";
        var db = CreateDbContext();
        var dataProtection = new EphemeralDataProtectionProvider();
        var credentials = Wayfarer.Tests.Infrastructure.CredentialTestFactory.Create(dataProtection);
        var profile = PersonalLocationProviderProfile.Create(userId, PersonalLocationProvider.Geoapify);
        credentials.Replace(profile, "credential");
        profile.SetAuthorization(PersonalProviderCapability.Routing, true);
        credentials.RecordVerification(profile, PersonalProviderCapability.Routing, PersonalProviderVerification.Verified);
        var selection = PersonalLocationProviderSelection.Create(userId);
        selection.Select(PersonalProviderCapability.Routing, PersonalLocationProvider.Geoapify);
        var planningProfile = db.Set<TransportProfile>().First(item => item.Key == "car");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = userId, Name = "Personal route" };
        // Route originals include the real owning Region/Trip dependency; keep this fixture relationally valid.
        var region = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, Name = "Anchors" };
        var from = Place(userId, region, 23.7, 37.9);
        var via = Place(userId, region, 23.75, 37.95);
        var to = Place(userId, region, 23.8, 38.0);
        var segment = new Segment
        {
            Id = Guid.NewGuid(), UserId = userId, Trip = trip, TripId = trip.Id, Mode = planningProfile.Key,
            TransportProfileId = planningProfile.Id, FromPlace = from, FromPlaceId = from.Id,
            ToPlace = to, ToPlaceId = to.Id
        };
        segment.Waypoints.Add(new SegmentWaypoint
        {
            Segment = segment, SegmentId = segment.Id, Place = via, PlaceId = via.Id, Position = 0
        });
        db.AddRange(profile, selection, trip, from, via, to, segment);
        db.SaveChanges();
        var aggregateTokens = new SegmentAggregateTokenService(dataProtection);
        var aggregateToken = aggregateTokens.Issue(userId, trip.Id, segment.Id, segment.RowVersion);
        RouteCoordinate[] anchors = [new(23.7, 37.9), new(23.75, 37.95), new(23.8, 38.0)];
        return new(userId, db, credentials, profile, selection, planningProfile, trip, segment, from, via, to,
            aggregateTokens, aggregateToken, new ExternalRouteProposalContextService(dataProtection),
            new AuthoritativeRoutingProviderResolver(db, credentials), anchors);
    }

    private static Place Place(string userId, Region region, double longitude, double latitude) => new()
    {
        Id = Guid.NewGuid(), UserId = userId, Region = region, RegionId = region.Id,
        Location = new Point(longitude, latitude) { SRID = 4326 }
    };

    private sealed record Fixture(
        string UserId, ApplicationDbContext Db, PersonalProviderCredentialService Credentials,
        PersonalLocationProviderProfile ProviderProfile, PersonalLocationProviderSelection Selection,
        TransportProfile PlanningProfile, Trip Trip, Segment Segment, Place From, Place Via, Place To,
        SegmentAggregateTokenService AggregateTokens, string AggregateToken,
        ExternalRouteProposalContextService ProposalContexts, AuthoritativeRoutingProviderResolver Resolver,
        IReadOnlyList<RouteCoordinate> Anchors);

    private sealed class CallbackRouteClient(Func<CancellationToken, Task>? callback = null, ProviderRouteResult? route = null) : IProviderRouteClient
    {
        public int Requests { get; private set; }
        public string? Mode { get; private set; }

        public async Task<ProviderRouteResult> RouteAsync(ResolvedRoutingProviderExecution execution,
            IReadOnlyList<RouteCoordinate> anchors, Func<CancellationToken, Task<bool>> validateAuthority,
            CancellationToken cancellationToken)
        {
            Requests++;
            Mode = execution.Profile;
            if (callback != null) await callback(cancellationToken);
            if (route != null) return route;
            return new(true, anchors, anchors, null, 1250, 360,
                [new("Continue", "straight", 0, anchors.Count - 1, 1250, 360)],
                Enumerable.Range(0, anchors.Count).ToArray());
        }
    }

    private sealed class AcceptingGeometryValidator : IProviderRouteGeometryValidator
    {
        public ProviderRouteValidationResult Validate(IReadOnlyList<RouteCoordinate> anchors,
            ProviderRouteResult providerRoute, CancellationToken cancellationToken) =>
            new(true, providerRoute.Geometry, Enumerable.Range(0, anchors.Count).ToArray(), null, providerRoute.Instructions);
    }
}
