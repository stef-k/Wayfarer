using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

public sealed partial class PersonalRouteSavePostgresTests
{
    /// <summary>The same original route authorizes generation and acceptance across three independent protected Notes saves.</summary>
    [PostgresFact]
    public async Task IndependentProposal_PreservesNotesBeforeDuringAndAfterGeneration()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        var credentials = CredentialTestFactory.Create(protection);
        var seed = await SeedAsync(user.Id, credentials);
        await using var db = fixture.CreateContext();
        var original = (await SegmentRouteReconciler.LoadAggregateAsync(db, seed.SegmentId))!;
        var tokens = new SegmentAggregateTokenService(protection);
        var routeToken = tokens.IssueRoute(user.Id, seed.TripId, original);
        await SaveProtectedNotesAsync(original, "original", "Before generation");
        var client = new NotesDuringGenerationClient(() => SaveProtectedNotesAsync(original, "Before generation", "During generation"));
        var generator = new ExternalRouteProposalGenerator(db, tokens, client, new ControlledGeometryValidator(),
            new ExternalRouteProposalContextService(protection), new RoutingRequestBudget(), new(db, credentials));
        var generated = await generator.GenerateAsync(user.Id, seed.TripId, seed.SegmentId,
            routeToken, "drive", CancellationToken.None, independentRoute: true);
        Assert.True(generated.Succeeded, generated.ErrorCode);
        await SaveProtectedNotesAsync(original, "During generation", "Before acceptance");
        await using var saveDb = fixture.CreateContext();
        using var body = await IndependentBodyAsync(seed.SegmentId, routeToken, generated.Proposal!);
        var saved = await SaveService(saveDb, protection, credentials).UpdateSegmentAsync(seed.TripId, seed.SegmentId,
            user.Id, body, null, CancellationToken.None);
        Assert.Equal(EditorRegionMutationStatus.Success, saved.Status);
        await using var verify = fixture.CreateContext();
        var persisted = await verify.Segments.AsNoTracking().SingleAsync(s => s.Id == seed.SegmentId);
        Assert.Equal("Before acceptance", persisted.Notes);
        Assert.Equal("geoapify", persisted.RouteProvider);
        Assert.Equal(1.25, persisted.EstimatedDistanceKm);
        Assert.NotNull(persisted.RouteGeometry);
    }

    /// <summary>New proposal contexts still reject drift, altered geometry, expiry and provider-authority generations atomically.</summary>
    [PostgresTheory]
    [InlineData("anchor")]
    [InlineData("expiry")]
    [InlineData("tamper")]
    [InlineData("authority")]
    public async Task IndependentProposal_RejectsInvalidEvidence(string defect)
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        var credentials = CredentialTestFactory.Create(protection);
        var seed = await SeedAsync(user.Id, credentials);
        await using var db = fixture.CreateContext();
        var original = (await SegmentRouteReconciler.LoadAggregateAsync(db, seed.SegmentId))!;
        var tokens = new SegmentAggregateTokenService(protection);
        var token = tokens.IssueRoute(user.Id, seed.TripId, original);
        var clock = new ProposalClock();
        var contexts = new ExternalRouteProposalContextService(protection, clock);
        var generator = new ExternalRouteProposalGenerator(db, tokens, new ControlledRouteClient(360),
            new ControlledGeometryValidator(), contexts, new RoutingRequestBudget(), new(db, credentials));
        var generated = await generator.GenerateAsync(user.Id, seed.TripId, seed.SegmentId, token, "drive", CancellationToken.None, true);
        Assert.True(generated.Succeeded, generated.ErrorCode);
        await using (var other = fixture.CreateContext())
        {
            if (defect == "anchor") await other.Places.Where(p => p.Id == original.FromPlaceId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Location, new NetTopologySuite.Geometries.Point(1, 1) { SRID = 4326 }));
            if (defect == "authority")
            {
                var selection = await other.PersonalLocationProviderSelections.SingleAsync(s => s.UserId == user.Id);
                selection.Select(Wayfarer.Models.LocationProviders.PersonalProviderCapability.Routing, null);
                await other.SaveChangesAsync();
            }
        }
        var proposal = generated.Proposal!;
        if (defect == "tamper") proposal = proposal with { Geometry = [new(1, 1), .. proposal.Geometry.Skip(1)] };
        if (defect == "expiry") clock.Now = proposal.ExpiresAt.AddTicks(1);
        await using var saveDb = fixture.CreateContext();
        var before = await saveDb.Segments.AsNoTracking().SingleAsync(s => s.Id == seed.SegmentId);
        var beforeTime = await saveDb.Trips.Where(t => t.Id == seed.TripId).Select(t => t.UpdatedAt).SingleAsync();
        using var body = await IndependentBodyAsync(seed.SegmentId, token, proposal, "original", "Combined Notes");
        var save = new TripEditorSegmentMutationService(saveDb, tokens, new(protection, TimeProvider.System),
            new ExternalRouteProposalSaveValidator(saveDb, tokens, contexts, new(saveDb, credentials)));
        var rejected = await save.UpdateSegmentAsync(seed.TripId, seed.SegmentId, user.Id, body, null, CancellationToken.None);
        Assert.NotEqual(EditorRegionMutationStatus.Success, rejected.Status);
        await using var verify = fixture.CreateContext();
        var persisted = await verify.Segments.AsNoTracking().SingleAsync(s => s.Id == seed.SegmentId);
        Assert.Equal(before.RowVersion, persisted.RowVersion);
        Assert.Equal(before.Notes, persisted.Notes);
        Assert.Null(persisted.RouteGeometry);
        Assert.Equal(beforeTime, await verify.Trips.Where(t => t.Id == seed.TripId).Select(t => t.UpdatedAt).SingleAsync());
    }

    /// <summary>Uses the existing protected Notes authority, with its own transaction and exact original.</summary>
    private async Task SaveProtectedNotesAsync(Segment segment, string? original, string desired)
    {
        await using var db = fixture.CreateContext();
        using var body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
            { expected = new { notesHtml = original }, changes = new { notesHtml = desired } }));
        await new SegmentNotesConditionalMutationService(db).ApplyAsync(segment.TripId, segment.Id, segment.UserId,
            body, true, null, CancellationToken.None);
    }

    /// <summary>Uses the existing effective proposal draft with the strict route dialect and optional authored Notes.</summary>
    private async Task<Stream> IndependentBodyAsync(Guid segmentId, string token, ExternalRouteProposalDto proposal,
        string? original = null, string? desired = null)
    {
        using var legacy = await BodyAsync(segmentId, "unused", proposal);
        var request = (await JsonNode.ParseAsync(legacy))!.AsObject();
        request.Remove("aggregateConcurrencyToken");
        request.Remove("notesHtml");
        request["contract"] = "segment-route-notes-1";
        request["originalRouteToken"] = token;
        if (desired != null)
        {
            request["expected"] = new JsonObject { ["notesHtml"] = original };
            request["changes"] = new JsonObject { ["notesHtml"] = desired };
        }
        return new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(request));
    }

    /// <summary>Changes Notes inside the provider boundary, then exercises the real generation current-authority callback.</summary>
    private sealed class NotesDuringGenerationClient(Func<Task> saveNotes) : IProviderRouteClient
    {
        public async Task<ProviderRouteResult> RouteAsync(ResolvedRoutingProviderExecution execution,
            IReadOnlyList<RouteCoordinate> anchors, Func<CancellationToken, Task<bool>> validateAuthority, CancellationToken token)
        {
            await saveNotes();
            Assert.True(await validateAuthority(token));
            return await new ControlledRouteClient(360).RouteAsync(execution, anchors, validateAuthority, token);
        }
    }

    /// <summary>Advances only the protected proposal's original expiry; no real-time waits or reissue.</summary>
    private sealed class ProposalClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
