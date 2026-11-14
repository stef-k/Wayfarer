using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Models;

namespace Wayfarer.Services;

public sealed partial class TripCompositionService
{
    /// <summary>Returns an authorized coherent live preview and per-object originals without persisting a workspace.</summary>
    public async Task<object> PreviewCopyAsync(string owner, Guid sourceTripId, Guid destinationTripId,
        Func<CancellationToken, Task<bool>>? credential = null, CancellationToken token = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var (source, current) = await CopyContextAsync(owner, sourceTripId, destinationTripId, [], credential, token);
        var own = source.UserId == owner;
        var omitted = !own && TripCopyMedia.OmitUnqualified(source);
        var projection = TripCompositionSnapshot.Project(source, own, omitted);
        var originals = TripCopyOriginals.Capture(source, own, projection);
        return new { sourceTripId, tripId = current.Id, source = System.Text.Json.JsonSerializer.SerializeToElement(projection, TripCompositionSnapshot.Json),
            current = TripCompositionSnapshot.Project(current, true), context = originals.Context, originals = originals.Objects };
    }

    /// <summary>Copies/adopts the complete reviewed selection through the composition core, committing dependencies and credits together.</summary>
    public async Task<object> CopyAsync(string owner, TripCopyRequest request,
        PlaceRegionLifecycleService lifecycle, SegmentAggregateTokenService routeTokens, SegmentRouteClearConfirmation routeConsent,
        IReadOnlySet<string> icons, IReadOnlySet<string> colors,
        Func<CancellationToken, Task<bool>>? credential = null, CancellationToken token = default)
    {
        if (request.Items == null || request.Items.Count is < 1 or > 128 || request.Items.Any(i => i == null)
            || request.Items.Sum(i => 1 + (i.Children?.Count ?? 0)) > 128) throw TripCompositionSource.Invalid();
        // This entry point has no removal/metadata/workspace decisions. Nested items obey the same boundary.
        if (TripCopyOriginals.Flatten(request.Items).Any(i => i == null || i.Kind == "trip" || i.Decision is not ("copy" or "reuse" or "skip")
            || i.ExpectedDecision != null || i.Children?.Any(c => c == null || c.Children != null) == true)) throw TripCompositionSource.Invalid();
        var routes = TripCompositionRouteInput.Validate(request.Items);
        SegmentRouteReconciler.EnsureCleanContext(db);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var committing = false;
        try
        {
            var (live, current) = await CopyContextAsync(owner, request.SourceTripId, request.DestinationTripId,
                routes.Values.Select(r => r.TransportProfileId).OfType<Guid>().ToArray(), credential, token);
            var previousUpdatedAt = current.UpdatedAt;
            var own = live.UserId == owner;
            var omitted = !own && TripCopyMedia.OmitUnqualified(live);
            var projection = TripCompositionSnapshot.Project(live, own, omitted);
            var originals = TripCopyOriginals.Capture(live, own, projection);
            TripCopyOriginals.Validate(request, originals.Context, originals.Objects);
            // Decode the same normalized authorized representation as retained composition; source graphs stay detached.
            var source = TripCompositionSource.Read(System.Text.Json.JsonSerializer.Serialize(projection, TripCompositionSnapshot.Json));
            var mappings = new List<TripCompositionMapping>();
            var apply = new TripCompositionApply(db, current, source, "b", mappings, lifecycle, routeTokens, routeConsent, icons, colors, routes);
            await apply.ApplyAsync(request.Items, token);
            await db.SaveChangesAsync(token);
            db.ChangeTracker.Clear();
            var saved = await DestinationAsync(current.Id, owner, false, token);
            var response = new TripCompositionApplied(Guid.Empty, current.Id, "copied", mappings,
                TripCompositionSnapshot.Project(saved, true), omitted, saved.UpdatedAt != previousUpdatedAt, saved.UpdatedAt);
            committing = true;
            await transaction.CommitAsync(token);
            return response;
        }
        catch (Exception primary)
        {
            db.ChangeTracker.Clear();
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception cleanup) { throw new AggregateException("Copy failed and rollback could not be confirmed.", primary, cleanup); }
            if (!committing && primary.GetBaseException() is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.UniqueViolation })
                throw new TripMetadataMutationRejected(409, "copy-contention", "Source or destination changed while saving. Keep your choices and explicitly refresh and review before another attempt.");
            throw;
        }
    }

    /// <summary>Orders against account erasure, workspace mutation and domain writers; final reads follow every wait.</summary>
    private async Task<(Trip Source, Trip Destination)> CopyContextAsync(string owner, Guid sourceId, Guid destinationId,
        Guid[] requestedProfiles, Func<CancellationToken, Task<bool>>? credential, CancellationToken token)
    {
        var ids = new[] { sourceId, destinationId }.Distinct().ToArray();
        var owners = await db.Trips.AsNoTracking().Where(t => ids.Contains(t.Id)).Select(t => t.UserId).Distinct().ToArrayAsync(token);
        foreach (var user in owners.Append(owner).Distinct().Order(StringComparer.Ordinal)) await LocationDeletionFence.AcquireAsync(db, user, token);
        await TripCompositionLifecycle.LockAsync(db, owner, destinationId, null, token);
        var construction = new TripCloneCoordinator(db);
        var source = await construction.LoadSourceAsync(sourceId, token) ?? throw Unavailable();
        var destination = await DestinationAsync(destinationId, owner, false, token);
        var profiles = source.Segments.Concat(destination.Segments).Select(s => s.TransportProfileId).OfType<Guid>()
            .Concat(requestedProfiles).Distinct().Order().ToArray();
        await LockGraphsAsync([source, destination], profiles, token);
        foreach (var user in owners.Append(owner).Distinct().Order(StringComparer.Ordinal)) await RequireActiveAsync(user, token);
        if (credential != null && !await credential(token)) throw Unavailable();
        var live = await construction.LoadSourceAsync(sourceId, token) ?? throw Unavailable();
        var current = await DestinationAsync(destinationId, owner, true, token);
        if (!owners.Contains(live.UserId) || live.UserId != owner && (!live.IsPublic || !TripReusePolicy.GrantsReuse(live))
            || current.Regions.Any(r => r.UserId != owner || r.Places.Any(p => p.UserId != owner)) || current.Segments.Any(s => s.UserId != owner)) throw Unavailable();
        if (!Identities(live).IsSubsetOf(Identities(source)) || !Identities(current).IsSubsetOf(Identities(destination))
            || live.Segments.Concat(current.Segments).Select(s => s.TransportProfileId).OfType<Guid>().Except(profiles).Any()) throw TripCompositionOrdering.Stale();
        return (live, current);
    }
}
