using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Models;

namespace Wayfarer.Services;

public sealed partial class TripCompositionService
{
    /// <summary>Applies one bounded reviewed action against retained source values and current owned C, with one outer commit.</summary>
    public async Task<object> ApplyAsync(string owner, Guid id, TripCompositionAction action,
        PlaceRegionLifecycleService lifecycle, SegmentAggregateTokenService routeTokens, SegmentRouteClearConfirmation routeConsent, IReadOnlySet<string> icons, IReadOnlySet<string> colors,
        Func<CancellationToken, Task<bool>>? credential = null, CancellationToken token = default)
    {
        if (action.Source is not ("a" or "b") || action.Items == null || action.Items.Count is < 1 or > 128 || action.Items.Any(i => i == null)
            || action.Items.Sum(i => 1 + (i.Children?.Count ?? 0)) > 128) throw TripCompositionOrdering.Invalid("Select 1–128 objects from one captured source.");
        SegmentRouteReconciler.EnsureCleanContext(db);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var committing = false;
        try
        {
            await LocationDeletionFence.AcquireAsync(db, owner, token);
            await TripCompositionLifecycle.LockAsync(db, owner, null, id, token);
            var workspace = await OwnedAsync(owner, id, token);
            if (workspace.FinishedAt != null) throw Closed();
            var source = TripCompositionSource.Read(action.Source == "a" ? workspace.SnapshotA! : workspace.SnapshotB!);
            var discovered = await DestinationAsync(workspace.TripId, owner, false, token);
            var profiles = discovered.Segments.Select(s => s.TransportProfileId).OfType<Guid>()
                .Concat(source.Trip.Segments.Select(s => s.TransportProfileId).OfType<Guid>())
                .Concat(RequestedProfiles(action.Items)).Distinct().Order().ToArray();
            await LockDestinationAsync(discovered, profiles, token);
            await RequireActiveAsync(owner, token);
            if (credential != null && !await credential(token)) throw Unavailable();
            var current = await DestinationAsync(workspace.TripId, owner, true, token);
            if (current.Regions.Any(r => r.UserId != owner || r.Places.Any(p => p.UserId != owner))
                || current.Segments.Any(s => s.UserId != owner)) throw Unavailable();
            // A new earlier-class dependency cannot be acquired backwards after the final Trip wait.
            if (!Identities(current).IsSubsetOf(Identities(discovered))
                || current.Segments.Select(s => s.TransportProfileId).OfType<Guid>().Except(profiles).Any()) throw TripCompositionOrdering.Stale();
            var mappings = JsonSerializer.Deserialize<List<TripCompositionMapping>>(workspace.Mappings!, TripCompositionSnapshot.Json)!;
            MarkRemoved(mappings, current);
            var apply = new TripCompositionApply(db, current, source, action.Source, mappings, lifecycle, routeTokens, routeConsent, icons, colors);
            await apply.ApplyAsync(action.Items, token);
            await db.SaveChangesAsync(token);
            if (apply.TagsChanged)
            {
                var now = DateTime.UtcNow;
                await db.Trips.Where(t => t.Id == current.Id && t.UserId == owner)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.UpdatedAt, t => t.UpdatedAt > now ? t.UpdatedAt : now), token);
            }
            db.ChangeTracker.Clear();
            var saved = await DestinationAsync(current.Id, owner, false, token);
            MarkRemoved(mappings, saved);
            var serialized = JsonSerializer.Serialize(mappings, TripCompositionSnapshot.Json);
            await db.Set<TripComposition>().Where(w => w.Id == id).ExecuteUpdateAsync(s => s.SetProperty(w => w.Mappings, serialized), token);
            var response = new TripCompositionApplied(id, current.Id, "inProgress", mappings,
                TripCompositionSnapshot.Project(saved, true), source.MediaOmitted, apply.TagsChanged || saved.UpdatedAt != discovered.UpdatedAt, saved.UpdatedAt);
            committing = true;
            await transaction.CommitAsync(token);
            return response;
        }
        catch (Exception primary)
        {
            db.ChangeTracker.Clear();
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception cleanup) { throw new AggregateException("Composition failed and rollback could not be confirmed.", primary, cleanup); }
            if (!committing && primary.GetBaseException() is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.UniqueViolation })
                throw new TripMetadataMutationRejected(409, "composition-contention", "C changed while saving. Keep your choices and explicitly review current C before another attempt.");
            throw;
        }
    }

    /// <summary>Private suggestions and manual search use captured inputs and current C; neither changes a product decision.</summary>
    public async Task<object> CandidatesAsync(string owner, Guid id, string source, string kind, Guid sourceId,
        string? search, Func<CancellationToken, Task<bool>>? credential = null, CancellationToken token = default)
    {
        if (source is not ("a" or "b") || search?.Length > 200) throw TripCompositionSource.Invalid();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await RequireActiveAsync(owner, token);
        var workspace = await OwnedAsync(owner, id, token);
        if (workspace.FinishedAt != null) throw Closed();
        if (credential != null && !await credential(token)) throw Unavailable();
        var captured = TripCompositionSource.Read(source == "a" ? workspace.SnapshotA! : workspace.SnapshotB!);
        var current = await DestinationAsync(workspace.TripId, owner, false, token);
        var mappings = JsonSerializer.Deserialize<List<TripCompositionMapping>>(workspace.Mappings!, TripCompositionSnapshot.Json)!;
        MarkRemoved(mappings, current);
        return new { candidates = TripCompositionMatching.Candidates(captured.Trip, current, source, kind, sourceId, mappings, search), limit = 20 };
    }

    /// <summary>Loads one current owned graph. Only the apply caller requests tracking, after its final locks.</summary>
    private async Task<Trip> DestinationAsync(Guid tripId, string owner, bool tracking, CancellationToken token)
    {
        var query = db.Trips.Include(t => t.Tags).Include(t => t.Regions).ThenInclude(r => r.Places)
            .Include(t => t.Regions).ThenInclude(r => r.Areas).Include(t => t.Segments).ThenInclude(s => s.Waypoints).AsSplitQuery();
        return await (tracking ? query : query.AsNoTracking()).SingleOrDefaultAsync(t => t.Id == tripId && t.UserId == owner, token) ?? throw Unavailable();
    }

    private static IEnumerable<Guid> RequestedProfiles(IReadOnlyList<TripCompositionItem> items)
    {
        foreach (var item in items)
        {
            if (item.Fields?.Route is { Operation: "edit" } route && route.Value.ValueKind == JsonValueKind.Object && route.Value.TryGetProperty("transportProfileId", out var value)
                && value.ValueKind != JsonValueKind.Null) yield return value.GetGuid();
            if (item.Children != null) foreach (var id in RequestedProfiles(item.Children)) yield return id;
        }
    }

    /// <summary>Locks the discovered destination classes once, before any mutation or route staging; no late whole-Trip reconciliation.</summary>
    private async Task LockDestinationAsync(Trip trip, Guid[] profiles, CancellationToken token)
    {
        await SegmentRouteReconciler.LockProfilesAsync(db, profiles, token);
        foreach (var id in trip.Segments.Select(s => s.Id).Order()) await SegmentRouteReconciler.LockSegmentAsync(db, id, token);
        foreach (var id in trip.Regions.SelectMany(r => r.Places).Select(p => p.Id).Order())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Places\" WHERE \"Id\" = {id} FOR UPDATE", token);
        foreach (var id in trip.Regions.SelectMany(r => r.Areas).Select(a => a.Id).Order())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Areas\" WHERE \"Id\" = {id} FOR UPDATE", token);
        foreach (var id in trip.Regions.Select(r => r.Id).Order())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Regions\" WHERE \"Id\" = {id} FOR UPDATE", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Trips\" WHERE \"Id\" = {trip.Id} FOR UPDATE", token);
    }

    internal static HashSet<(string Kind, Guid Id)> Identities(Trip current) => current.Regions.Select(r => ("region", r.Id))
        .Concat(current.Regions.SelectMany(r => r.Places).Select(p => ("place", p.Id)))
        .Concat(current.Regions.SelectMany(r => r.Areas).Select(a => ("area", a.Id)))
        .Concat(current.Segments.Select(s => ("segment", s.Id))).Append(("trip", current.Id)).ToHashSet();

    internal static void MarkRemoved(List<TripCompositionMapping> mappings, Trip current)
    {
        var present = Identities(current);
        for (var i = 0; i < mappings.Count; i++)
            if (mappings[i].Decision is not ("skip" or "destinationRemoved") && !present.Contains((mappings[i].Kind, mappings[i].DestinationId)))
                mappings[i] = mappings[i] with { Decision = "destinationRemoved" };
    }
}

/// <summary>Saved response with private server-side post-commit freshness data excluded from transport.</summary>
internal sealed record TripCompositionApplied(Guid CompositionId, Guid TripId, string Status,
    IReadOnlyList<TripCompositionMapping> Mappings, object Current, bool MediaOmitted,
    [property: System.Text.Json.Serialization.JsonIgnore] bool PublicChanged,
    [property: System.Text.Json.Serialization.JsonIgnore] DateTime UpdatedAt);
