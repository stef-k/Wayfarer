using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

public sealed partial class TripCompositionPostgresTests
{
    /// <summary>An unchanged authored route must retain the database-owned accepted estimates.</summary>
    [PostgresTheory]
    [InlineData(false, "same")]
    [InlineData(true, "same")]
    [InlineData(false, "manual")]
    [InlineData(true, "manual")]
    [InlineData(false, "automatic")]
    [InlineData(true, "automatic")]
    [InlineData(false, "geometry")]
    [InlineData(true, "geometry")]
    public async Task Review_UnchangedRouteRetainsMeasurements(bool provider, string change)
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext();
        if (provider)
        {
            await db.Segments.Where(s => s.TripId == a.Id).ExecuteUpdateAsync(s => s
                .SetProperty(s => s.RouteProvider, "geoapify").SetProperty(s => s.RouteStorageMode, "persistent")
                .SetProperty(s => s.EstimatedDurationSource, change == "automatic" ? EstimatedDurationSource.Manual : EstimatedDurationSource.Automatic));
        }
        var created = await StartSelection(db, a, b);
        var c = await Load(db, created.TripId); var target = c.Segments.Single();
        var canonical = await SegmentRouteReconciler.LoadAggregateAsync(db, target.Id) ?? throw new Exception();
        var proof = new SegmentAggregateTokenService(selectionProtection).IssueRoute(a.UserId, c.Id, canonical);
        db.ChangeTracker.Clear();
        var anchors = c.Regions.SelectMany(r => r.Places).Where(p => p.Location != null)
            .Select(p => new TripCompositionAnchor(Guid.Empty, p.Id, p.RegionId, [p.Location!.X, p.Location.Y])).ToArray();
        var route = E(new { target.FromPlaceId, target.ToPlaceId,
            waypoints = target.Waypoints.OrderBy(w => w.Position).Select(w => new { w.PlaceId, w.Position, w.RouteVertexIndex }),
            routeJson = change == "geometry" ? "{\"type\":\"LineString\",\"coordinates\":[[12,40],[13,41],[13.5,42],[14,42]]}"
                : new NetTopologySuite.IO.GeoJsonWriter().Write(target.RouteGeometry), target.Mode, target.TransportProfileId,
            durationSource = change == "manual" ? "Manual" : change == "automatic" ? "Automatic" : target.EstimatedDurationSource.ToString(),
            manualDurationMinutes = change == "manual" ? 5.0 : change == "automatic" || provider ? (double?)null : target.EstimatedDuration?.TotalMinutes });
        await Select(db, a.UserId, created, "b", [new("segment", b.Segments.Single().Id, "reuse", DestinationId: target.Id,
            ConditionToken: proof, Anchors: anchors, Fields: new(Route: new("edit", E(TripCompositionFieldValues.Route(target)), route)))]);
        var saved = (await Load(db, c.Id)).Segments.Single();
        if (change == "same")
        {
            Assert.Equal(target.EstimatedDuration, saved.EstimatedDuration);
            Assert.Equal(target.EstimatedDistanceKm, saved.EstimatedDistanceKm);
            Assert.Equal(SegmentRouteProvenance.Capture(target), SegmentRouteProvenance.Capture(saved));
        }
        else if (change == "manual")
        {
            Assert.Equal(TimeSpan.FromMinutes(5), saved.EstimatedDuration);
            Assert.Equal(EstimatedDurationSource.Manual, saved.EstimatedDurationSource);
            Assert.Equal(target.EstimatedDistanceKm, saved.EstimatedDistanceKm);
            Assert.Equal(SegmentRouteProvenance.Capture(target), SegmentRouteProvenance.Capture(saved));
        }
        else if (change == "automatic")
        {
            Assert.Equal(EstimatedDurationSource.Automatic, saved.EstimatedDurationSource);
            Assert.NotEqual(target.EstimatedDuration, saved.EstimatedDuration);
            if (provider) Assert.Null(saved.EstimatedDuration);
        }
        else
        {
            Assert.NotEqual(target.EstimatedDistanceKm, saved.EstimatedDistanceKm);
            Assert.Null(saved.RouteProvider);
            if (!provider) Assert.Equal(target.EstimatedDuration, saved.EstimatedDuration);
        }
        Assert.Equal(target.Title, saved.Title); Assert.Equal(target.Notes, saved.Notes);
    }

    /// <summary>Malformed nested values must be rejected by ordinary product validation.</summary>
    [PostgresTheory]
    [InlineData("profile")]
    [InlineData("anchor")]
    public async Task Review_InvalidValuesAreValidationFailures(string scenario)
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext(); var created = await StartSelection(db, a, b);
        var c = await Load(db, created.TripId); var target = c.Segments.Single();
        var item = new TripCompositionItem("segment", b.Segments.Single().Id, "reuse", DestinationId: target.Id,
            Anchors: scenario == "anchor" ? [null!, null!, null!] : [], Fields: new(Route: new("edit", E(TripCompositionFieldValues.Route(target)),
                scenario == "profile" ? E(new { transportProfileId = 1 }) : E(new { target.FromPlaceId, target.ToPlaceId,
                    waypoints = target.Waypoints.Select(w => new { w.PlaceId, w.Position, w.RouteVertexIndex }), routeJson = (string?)null,
                    target.Mode, target.TransportProfileId, durationSource = "Manual", manualDurationMinutes = 5 }))));
        var error = await Record.ExceptionAsync(() => Select(db, a.UserId, created, "b", [item]));
        Assert.True(error is TripMetadataMutationRejected or JsonException,
            "Expected a normal validation exception; got " + error?.GetType().FullName + ": " + error?.Message);
    }

    /// <summary>A token from the ordinary editor approves clearing; composition must enforce that consequence.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_OrdinaryRouteClearConsentKeepsItsMeaning(bool authored)
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext(); var created = await StartSelection(db, a, b);
        var c = await Load(db, created.TripId); var target = c.Segments.Single();
        var region = c.Regions.Single();
        var alternate = new Place { Id = Guid.NewGuid(), RegionId = region.Id, UserId = a.UserId, Name = "Alternative Via", Location = new NetTopologySuite.Geometries.Point(13,41) { SRID = 4326 } };
        db.Places.Add(alternate); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var canonical = await SegmentRouteReconciler.LoadAggregateAsync(db, target.Id) ?? throw new Exception();
        var tokens = new SegmentAggregateTokenService(selectionProtection);
        var consent = new SegmentRouteClearConfirmation(selectionProtection, TimeProvider.System);
        var proof = tokens.IssueRoute(a.UserId, c.Id, canonical);
        var request = E(new { contract = "segment-route-notes-1", originalRouteToken = proof, target.FromPlaceId, target.ToPlaceId,
            waypointPlaceIds = new[] { alternate.Id }, waypointRouteVertexIndices = new[] { 1 }, mode = canonical.Mode,
            estimatedDurationSource = "Manual", estimatedDistanceKm = (double?)null, estimatedDurationMinutes = target.EstimatedDuration?.TotalMinutes,
            route = new { type = "LineString", coordinates = new[] {new[]{12.0,40.0},new[]{13.0,41.0},new[]{14.0,42.0}} } });
        db.ChangeTracker.Clear();
        var challenge = await new TripEditorSegmentMutationService(db, tokens, consent).UpdateSegmentAsync(c.Id, target.Id, a.UserId,
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(request.GetRawText())), null, default);
        Assert.True(challenge.Status == EditorRegionMutationStatus.Conflict, E(challenge).GetRawText());
        var confirmation = Assert.IsType<EditorSegmentConflictDto>(challenge.Conflict);
        Assert.Equal("segment-route-clear-confirmation-required", confirmation.Code);
        
        db.ChangeTracker.Clear();
        var anchors = b.Regions.Single().Places.Where(p => p.Location != null).Select(p => {
            var to = p.Name == "Via" ? alternate : c.Regions.Single().Places.Single(x=>x.Name==p.Name);
            return new TripCompositionAnchor(p.Id,to.Id,to.RegionId,[to.Location!.X,to.Location.Y]);
        }).ToArray();
        var routeEdit = E(new { target.FromPlaceId, target.ToPlaceId,
            waypoints = new[] { new { placeId = alternate.Id, position = 0, routeVertexIndex = 1 } },
            routeJson = new NetTopologySuite.IO.GeoJsonWriter().Write(target.RouteGeometry),
            target.Mode, target.TransportProfileId, durationSource = "Manual", manualDurationMinutes = target.EstimatedDuration?.TotalMinutes });
        var item = new TripCompositionItem("segment", b.Segments.Single().Id, "reuse", DestinationId: target.Id,
            ConditionToken: proof, RouteClearToken: confirmation.ConfirmationToken, Anchors: anchors,
            Fields: new(Route: new(authored ? "edit" : "useSource", E(TripCompositionFieldValues.Route(target)), authored ? routeEdit : default)));
        var before = await db.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == created.CompositionId);
        // Invalid consent and a different proposed duration cannot reuse the real challenge.
        if (authored)
        {
            await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => Select(db, a.UserId, created, "b", [item with { RouteClearToken = "invalid" }]));
            var changed = E(new { target.FromPlaceId, target.ToPlaceId,
                waypoints = new[] { new { placeId = alternate.Id, position = 0, routeVertexIndex = 1 } },
                routeJson = new NetTopologySuite.IO.GeoJsonWriter().Write(target.RouteGeometry),
                target.Mode, target.TransportProfileId, durationSource = "Manual", manualDurationMinutes = 15.0 });
            await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => Select(db, a.UserId, created, "b", [item with
                { Fields = new(Route: new("edit", E(TripCompositionFieldValues.Route(target)), changed)) }]));
        }
        else await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => Select(db, a.UserId, created, "b", [item]));
        var unchanged = (await Load(db, c.Id)).Segments.Single();
        Assert.True(TripCompositionFieldValues.Equal(E(TripCompositionFieldValues.Route(target)), TripCompositionFieldValues.Route(unchanged)));
        Assert.Equal(before.Mappings, (await db.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == before.Id)).Mappings);
        if (authored) await Select(db, a.UserId, created, "b", [item]);
        else
        {
            // The same real consent still performs the ordinary approved clear after composition refused source adoption.
            db.ChangeTracker.Clear();
            var ordinary = await new TripEditorSegmentMutationService(db, tokens, consent).UpdateSegmentAsync(c.Id, target.Id, a.UserId,
                new MemoryStream(System.Text.Encoding.UTF8.GetBytes(request.GetRawText())), confirmation.ConfirmationToken, default);
            Assert.Equal(EditorRegionMutationStatus.Success, ordinary.Status);
        }
        var saved = (await Load(db, c.Id)).Segments.Single();
        Assert.Null(saved.RouteGeometry);
        Assert.Null(saved.Waypoints.Single().RouteVertexIndex);
        Assert.Equal(alternate.Id, saved.Waypoints.Single().PlaceId);
        Assert.Equal(target.EstimatedDuration, saved.EstimatedDuration);
        Assert.NotEqual(target.EstimatedDistanceKm, saved.EstimatedDistanceKm);
        Assert.Null(saved.RouteProvider);
        Assert.Equal(target.Title, saved.Title); Assert.Equal(target.Notes, saved.Notes);
        Assert.Equal(target.DisplayOrder, saved.DisplayOrder);

    }

    /// <summary>Unknown members of a route waypoint must not silently disappear during authored saves.</summary>
    [PostgresFact]
    public async Task Review_UnknownWaypointMemberRefuses()
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext(); var created = await StartSelection(db,a,b);
        var c = await Load(db,created.TripId); var target = c.Segments.Single();
        var canonical = await SegmentRouteReconciler.LoadAggregateAsync(db,target.Id) ?? throw new Exception();
        var proof = new SegmentAggregateTokenService(selectionProtection).IssueRoute(a.UserId,c.Id,canonical); db.ChangeTracker.Clear();
        var anchors = c.Regions.SelectMany(r=>r.Places).Where(p=>p.Location!=null)
            .Select(p=>new TripCompositionAnchor(Guid.Empty,p.Id,p.RegionId,[p.Location!.X,p.Location.Y])).ToArray();
        var route = E(new { target.FromPlaceId,target.ToPlaceId,waypoints = target.Waypoints.Select(w=>new {w.PlaceId,w.Position,w.RouteVertexIndex,unexpected="must refuse"}),
            routeJson = new NetTopologySuite.IO.GeoJsonWriter().Write(target.RouteGeometry),target.Mode,target.TransportProfileId,durationSource="Manual",manualDurationMinutes=5 });
        var error = await Record.ExceptionAsync(()=>Select(db,a.UserId,created,"b",[new("segment",b.Segments.Single().Id,"reuse",DestinationId:target.Id,
            ConditionToken:proof,Anchors:anchors,Fields:new(Route:new("edit",E(TripCompositionFieldValues.Route(target)),route)))]));
        Assert.True(error is TripMetadataMutationRejected or JsonException,"Expected strict validation; the unknown waypoint member was accepted.");
    }

}
