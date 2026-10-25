using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

/// <summary>Bounded Region vector compare/apply authority shared by Web and bearer; never writes children or canonical order.</summary>
internal sealed class RegionOrderingService(ApplicationDbContext db, LifecycleDependencyConfirmation confirmation)
{
    /// <summary>Checks retained public originals before issuing private raw-content conditions; never rebases a stale draft.</summary>
    internal async Task<object> PrepareAsync(Guid tripId, string userId, RegionOrderRequest request, bool web, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var regions = await ReadAsync(tripId, userId, token);
        Compare(request, regions);
        return new { contract = "bd2-1", operation = "region-order", tripId, requiresReview = true,
            regions = regions.Where(r => !RegionOrderRequest.IsCanonical(r)).Select(RegionOrderRequest.Original),
            canonical = RegionOrderRequest.CanonicalOriginal(regions),
            conditionToken = confirmation.IssueRegionOrder(userId, tripId, request.Intent(web), Fingerprint(regions)) };
    }

    /// <summary>Locks sorted Regions before Trip, rediscovers scope/authority, then writes normal orders and freshness atomically.</summary>
    internal async Task<EditorMutationResult<object>> ApplyAsync(Guid tripId, string userId, RegionOrderRequest request, bool web,
        Func<CancellationToken, Task<bool>>? authority, CancellationToken token)
    {
        if (db.ChangeTracker.Entries().Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Region ordering cannot save unrelated tracked changes.");
        var candidates = await ReadAsync(tripId, userId, token);
        Compare(request, candidates);
        if (!request.Reviewed || string.IsNullOrWhiteSpace(request.ConditionToken))
            throw new TripMetadataMutationRejected(409, "mutation-review-required", "Keep your order and review its original conditions before applying.");
        if (!confirmation.MatchesRegionOrder(request.ConditionToken, userId, tripId, request.Intent(web), Fingerprint(candidates))) throw Stale();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var commitStarted = false;
        try
        {
            foreach (var region in candidates.OrderBy(r => r.Id))
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Regions\" WHERE \"Id\" = {region.Id} FOR UPDATE", token);
            var trip = await db.Trips.FromSqlInterpolated($"SELECT * FROM public.\"Trips\" WHERE \"Id\" = {tripId} AND \"UserId\" = {userId} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(token);
            if (trip == null) throw Unavailable();
            var active = await db.Users.FromSqlInterpolated($"SELECT * FROM public.\"AspNetUsers\" WHERE \"Id\" = {userId} AND \"IsActive\" FOR SHARE")
                .AsNoTracking().AnyAsync(token);
            if (!active || authority != null && !await authority(token))
                throw new TripMetadataMutationRejected(401, "mutation-authority-unavailable", "Authentication is no longer valid.");
            var current = await ReadAsync(tripId, userId, token);
            Compare(request, current);
            if (!confirmation.MatchesRegionOrder(request.ConditionToken, userId, tripId, request.Intent(web), Fingerprint(current))) throw Stale();
            var normal = current.Where(r => !RegionOrderRequest.IsCanonical(r)).ToDictionary(r => r.Id);
            for (var index = 0; index < request.RegionIds.Length; index++)
            {
                var region = normal[request.RegionIds[index]];
                var order = index + 1;
                await db.Regions.Where(r => r.Id == region.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.DisplayOrder, order), token);
                region.DisplayOrder = order;
            }
            var now = DateTime.UtcNow;
            await db.Trips.Where(t => t.Id == tripId).ExecuteUpdateAsync(s => s.SetProperty(t => t.UpdatedAt, t => t.UpdatedAt > now ? t.UpdatedAt : now), token);
            var orderIds = current.OrderBy(r => r.DisplayOrder).ThenBy(r => r.Id).Select(r => r.Id).ToArray();
            var affected = new EditorAffectedSlicesDto(null, normal.Values.Select(EditorTripStateMapper.ToRegion).ToArray(), orderIds,
                [], new Dictionary<Guid, IReadOnlyList<Guid>>(), [], new Dictionary<Guid, IReadOnlyList<Guid>>(), [], null, [], null, null, null);
            var result = new EditorMutationResult<object>(true, new { tripId, regionOrder = orderIds }, affected, EditorDeletedIdsDto.Empty, []) { Contract = "bd2-1" };
            commitStarted = true;
            await transaction.CommitAsync(token);
            return result;
        }
        catch (Exception original)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception rollback) { throw new AggregateException("Region ordering failed and rollback could not be confirmed.", original, rollback); }
            if (!commitStarted && IsContention(original))
                throw new TripMetadataMutationRejected(409, "mutation-contention", "The order was not applied. Keep your draft and review current state.");
            throw;
        }
    }

    /// <summary>Reads the entire Trip membership, rejecting malformed ownership without filtering it into a plausible vector.</summary>
    private async Task<Region[]> ReadAsync(Guid tripId, string userId, CancellationToken token)
    {
        if (!await db.Trips.AsNoTracking().AnyAsync(t => t.Id == tripId && t.UserId == userId, token)) throw Unavailable();
        var regions = await db.Regions.AsNoTracking().Where(r => r.TripId == tripId).OrderBy(r => r.Id).ToArrayAsync(token);
        if (regions.Any(r => r.UserId != userId) || regions.Count(RegionOrderRequest.IsCanonical) > 1) throw Stale();
        return regions;
    }

    /// <summary>Public material, exact membership/stored orders and canonical presence must still match the client base.</summary>
    private static void Compare(RegionOrderRequest request, Region[] regions)
    {
        var originals = request.Regions.EnumerateArray().OrderBy(r => r.GetProperty("id").GetGuid()).ToArray();
        var normal = regions.Where(r => !RegionOrderRequest.IsCanonical(r)).ToArray();
        if (!PlaceStructuralRequest.Equal(System.Text.Json.JsonSerializer.SerializeToElement(originals), normal.Select(RegionOrderRequest.Original).ToArray())
            || !PlaceStructuralRequest.Equal(request.Canonical, RegionOrderRequest.CanonicalOriginal(regions))) throw Stale();
    }

    /// <summary>Raw scalar content prevents normalized public values from authorizing same-ID material replacement.</summary>
    private static string Fingerprint(Region[] regions) => PlaceStructuralRequest.Hash(regions.Select(r => RegionOrderRequest.IsCanonical(r)
        ? (object)new { r.Id, r.UserId, r.TripId, r.DisplayOrder, Canonical = true }
        : new { r.Id, r.UserId, r.TripId, r.DisplayOrder, r.Name, r.Notes, r.CoverImageUrl,
            Center = r.Center == null ? null : Convert.ToHexString(r.Center.AsBinary()), Srid = r.Center?.SRID }));

    /// <summary>Only proven rolled-back PostgreSQL transaction aborts become definitive contention responses.</summary>
    private static bool IsContention(Exception failure)
    {
        for (Exception? cause = failure; cause != null; cause = cause.InnerException)
            if (cause is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected }) return true;
        return false;
    }

    /// <summary>Conceals absent/foreign scope without returning current values.</summary>
    private static TripMetadataMutationRejected Unavailable() => new(404, "mutation-scope-unavailable", "Trip not found.");
    /// <summary>Definitive no-write conflict retains original intent for deliberate resolution.</summary>
    private static TripMetadataMutationRejected Stale() => new(409, "mutation-base-stale", "The original Region content, membership or order changed. Keep your draft and explicitly review a new base.");
}
