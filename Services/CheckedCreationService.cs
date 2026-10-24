using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Configuration;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Services;

/// <summary>Shared checked append authority for Region and Place creation; each call owns one atomic transaction.</summary>
internal sealed class CheckedCreationService(ApplicationDbContext db)
{
    /// <summary>Appends one server-identified Region without normalizing historical sibling orders.</summary>
    internal Task<T> RegionAsync<T>(Guid tripId, string userId, CreationConditionalRequest request, Region region,
        Func<Region, IReadOnlyList<Guid>, Task<T>> project, Func<CancellationToken, Task<bool>>? authority, CancellationToken token) =>
        ExecuteAsync(tripId, userId, request, false, async regions =>
        {
            CompareRegions(request, regions, userId);
            region.Id = Guid.NewGuid();
            region.TripId = tripId;
            region.UserId = userId;
            region.DisplayOrder = Next(regions.Where(r => r.Name != "Unassigned Places").Select(r => (int?)r.DisplayOrder));
            db.Regions.Add(region);
            await SaveAsync(tripId, token);
            return await project(region, regions.Append(region).OrderBy(r => r.DisplayOrder).ThenBy(r => r.Id).Select(r => r.Id).ToArray());
        }, authority, token);

    /// <summary>Appends a Place and, only against accepted absence, stages the canonical fallback in the same transaction.</summary>
    internal Task<T> PlaceAsync<T>(Guid tripId, string userId, CreationConditionalRequest request, Place place,
        Func<Place, Task<T>> project, Func<CancellationToken, Task<bool>>? authority, CancellationToken token) =>
        ExecuteAsync(tripId, userId, request, true, async regions =>
        {
            Region destination;
            if (request.RegionId.HasValue)
            {
                destination = regions.SingleOrDefault(r => r.Id == request.RegionId) ?? throw Stale();
                if (destination.UserId != userId) throw Unavailable();
            }
            else
            {
                CompareRegions(request, regions, userId);
                if (regions.Any(r => r.Name == "Unassigned Places")) throw Stale();
                destination = new Region { Id = Guid.NewGuid(), TripId = tripId, UserId = userId, Name = "Unassigned Places", DisplayOrder = 0 };
                db.Regions.Add(destination);
            }
            var siblings = await db.Places.AsNoTracking().Where(p => p.RegionId == destination.Id).ToArrayAsync(token);
            if (siblings.Any(p => p.UserId != userId) || !siblings.Select(p => new CreationPlaceOriginal(p.Id, p.DisplayOrder)).OrderBy(p => p.Id)
                    .SequenceEqual(request.Places!.OrderBy(p => p.Id))) throw Stale();
            place.Id = place.Id == Guid.Empty ? Guid.NewGuid() : place.Id;
            place.UserId = userId;
            place.RegionId = destination.Id;
            // Existing parents remain untracked; only the new fallback joins the creation graph.
            if (!request.RegionId.HasValue) place.Region = destination;
            place.DisplayOrder = Next(siblings.Select(p => p.DisplayOrder));
            db.Places.Add(place);
            await SaveAsync(tripId, token);
            return await project(place);
        }, authority, token);

    /// <summary>Locks children before Regions before Trip, with serializable phantom detection and no internal replay.</summary>
    private async Task<T> ExecuteAsync<T>(Guid tripId, string userId, CreationConditionalRequest request, bool place,
        Func<Region[], Task<T>> append, Func<CancellationToken, Task<bool>>? authority, CancellationToken token)
    {
        if (db.ChangeTracker.Entries().Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Creation cannot save unrelated tracked changes.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var commitStarted = false;
        var before = db.ChangeTracker.Entries().Select(e => e.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        try
        {
            // Existing lifecycle mutations take sorted Place locks before sorted Region locks.
            if (place && request.RegionId.HasValue)
            {
                var ids = await db.Places.AsNoTracking().Where(p => p.RegionId == request.RegionId).Select(p => p.Id).ToArrayAsync(token);
                foreach (var id in ids.Order())
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Places\" WHERE \"Id\" = {id} FOR UPDATE", token);
            }
            var regionIds = await db.Regions.AsNoTracking().Where(r => r.TripId == tripId && (!place || request.RegionId == null || r.Id == request.RegionId))
                .Select(r => r.Id).ToArrayAsync(token);
            foreach (var id in regionIds.Order())
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Regions\" WHERE \"Id\" = {id} FOR UPDATE", token);
            var trip = await db.Trips.FromSqlInterpolated($"SELECT * FROM public.\"Trips\" WHERE \"Id\" = {tripId} AND \"UserId\" = {userId} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(token);
            if (trip == null) throw Unavailable();
            var active = await db.Users.FromSqlInterpolated($"SELECT * FROM public.\"AspNetUsers\" WHERE \"Id\" = {userId} AND \"IsActive\" FOR SHARE")
                .AsNoTracking().AnyAsync(token);
            if (!active || authority != null && !await authority(token))
                throw new TripMetadataMutationRejected(401, "mutation-authority-unavailable", "Authentication is no longer valid.");
            var regions = await db.Regions.AsNoTracking().Where(r => r.TripId == tripId && (!place || request.RegionId == null || r.Id == request.RegionId)).ToArrayAsync(token);
            var result = await append(regions);
            var freshness = trip.UpdatedAt > DateTime.UtcNow ? trip.UpdatedAt : DateTime.UtcNow;
            await db.Trips.Where(t => t.Id == tripId).ExecuteUpdateAsync(s => s.SetProperty(t => t.UpdatedAt, t => t.UpdatedAt > freshness ? t.UpdatedAt : freshness), token);
            commitStarted = true;
            await transaction.CommitAsync(token);
            return result;
        }
        catch (Exception original)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception rollback) { throw new AggregateException("Creation failed and rollback could not be confirmed.", original, rollback); }
            // A failed fallback graph must never survive into a recovery attempt or another caller save.
            foreach (var entry in db.ChangeTracker.Entries().Where(e => !before.Contains(e.Entity)).ToArray()) entry.State = EntityState.Detached;
            if (!commitStarted && IsUnappliedContention(original))
                throw new TripMetadataMutationRejected(409, "mutation-contention", "Creation was not applied. Review current parent and order before a new attempt.");
            throw;
        }
    }

    /// <summary>Uses the existing narrow Trip stamping owner and excludes caller-owned dirty work.</summary>
    private async Task SaveAsync(Guid tripId, CancellationToken token)
    {
        if (db.ChangeTracker.Entries().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Creation cannot save unrelated tracked changes.");
        await db.SaveChangesAsync(token);
        var now = DateTime.UtcNow;
        await db.Trips.Where(t => t.Id == tripId).ExecuteUpdateAsync(s => s.SetProperty(t => t.UpdatedAt, t => t.UpdatedAt > now ? t.UpdatedAt : now), token);
    }

    /// <summary>Canonical identity and order are dependencies; unrelated sibling content is not.</summary>
    private static void CompareRegions(CreationConditionalRequest request, Region[] regions, string userId)
    {
        if (regions.Any(r => r.UserId != userId) || !regions.Select(r => new CreationRegionOriginal(r.Id, r.DisplayOrder, r.Name == "Unassigned Places"))
                .OrderBy(r => r.Id).SequenceEqual(request.Regions!.OrderBy(r => r.Id))) throw Stale();
    }

    /// <summary>Appends after the maximum stored nonnull order; existing nulls/ties/gaps are preserved.</summary>
    private static int Next(IEnumerable<int?> orders)
    {
        var maximum = orders.Max() ?? 0;
        if (maximum == int.MaxValue) throw CreationConditionalRequest.Invalid("No append position is available.");
        return maximum + 1;
    }

    /// <summary>Reuses the exact canonical uniqueness recovery boundary, but never adopts a concurrent winner as an original.</summary>
    private static bool IsUnappliedContention(Exception failure)
    {
        var postgres = failure as PostgresException ?? (failure as DbUpdateException)?.InnerException as PostgresException;
        return postgres is { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected }
            or { SqlState: PostgresErrorCodes.UniqueViolation, SchemaName: "public", TableName: "Regions", ConstraintName: RegionConfiguration.UnassignedIndex };
    }

    /// <summary>Conceals missing and foreign Trip scope.</summary>
    private static TripMetadataMutationRejected Unavailable() => new(404, "mutation-scope-unavailable", "Creation scope not found.");
    /// <summary>Definitive rejection never leaks current membership or content.</summary>
    private static TripMetadataMutationRejected Stale() => new(409, "mutation-base-stale", "The original parent, membership or order changed. Keep the draft and explicitly review a new base.");
}
