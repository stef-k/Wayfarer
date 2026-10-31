using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Planning;

namespace Wayfarer.Services;

/// <summary>Narrow planning compare/apply boundary using the existing child-before-parent row barriers.</summary>
internal sealed class PlanningMutationService(ApplicationDbContext db)
{
    /// <summary>Revalidates membership, active authority and complete originals after waits; commits only Planning.</summary>
    internal async Task<PlanningMutationResult> ApplyAsync(Guid tripId, string kind, Guid targetId, string userId, Stream body,
        Func<CancellationToken, Task<bool>>? bearer, CancellationToken token)
    {
        var request = await PlanningMutationRequest.ReadAsync(body, token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var committing = false;
        try
        {
            var (target, parent) = await LockTargetAsync(tripId, kind, targetId, userId, token);
            var trip = await LockAsync<Trip>(tripId, token);
            if (trip == null || trip.UserId != userId) throw Unavailable();
            var users = await db.Users.FromSqlInterpolated($"SELECT * FROM public.\"AspNetUsers\" WHERE \"Id\" = {userId} AND \"IsActive\" FOR SHARE")
                .AsNoTracking().ToListAsync(token);
            if (users.Count == 0 || bearer != null && !await bearer(token))
                throw new TripMetadataMutationRejected(401, "mutation-authority-unavailable", "Authentication is no longer valid.");
            if (parent != request.ParentId) throw new TripMetadataMutationRejected(409, "mutation-scope-stale", "The object parent changed. Retain the draft and review its current scope.");
            if (!PlanningCodec.Equal(request.Original, target.Planning))
                throw new TripMetadataMutationRejected(409, "mutation-base-stale", "Planning changed. Retain the draft and explicitly review the current aggregate before saving.");
            if (request.Value != null)
            {
                try { PlanningValidation.Validate(request.Value, kind, target.Planning); }
                catch (PlanningValidationException error)
                { throw new TripMetadataMutationRejected(400, "planning-value-invalid", error.Message, new() { ["planning"] = [error.Message] }); }
            }
            var applied = !PlanningCodec.Equal(target.Planning, request.Value);
            if (applied) await WriteAsync(kind, targetId, request.Value, token);
            committing = true;
            await transaction.CommitAsync(token);
            return new("planning-1", true, applied, kind, targetId, parent, request.Value);
        }
        catch (Exception primary)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception rollback) { throw new AggregateException("Planning failed and rollback also failed.", primary, rollback); }
            if (!committing && primary is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure })
                throw new TripMetadataMutationRejected(409, "mutation-contention", "Planning contention prevented this attempt. Retain the draft and review current values.");
            throw;
        }
    }

    /// <summary>Uses fixed type/table pairs; no untrusted SQL identifiers or tracked pre-wait values.</summary>
    private Task<T?> LockAsync<T>(Guid id, CancellationToken token) where T : class
    {
        // Only compile-time graph types select an identifier; values remain database parameters.
        var sql = typeof(T) == typeof(Trip) ? "SELECT * FROM public.\"Trips\" WHERE \"Id\" = {0} FOR UPDATE"
            : typeof(T) == typeof(Region) ? "SELECT * FROM public.\"Regions\" WHERE \"Id\" = {0} FOR UPDATE"
            : typeof(T) == typeof(Place) ? "SELECT * FROM public.\"Places\" WHERE \"Id\" = {0} FOR UPDATE"
            : typeof(T) == typeof(Area) ? "SELECT * FROM public.\"Areas\" WHERE \"Id\" = {0} FOR UPDATE"
            : typeof(T) == typeof(Segment) ? "SELECT * FROM public.\"Segments\" WHERE \"Id\" = {0} FOR UPDATE"
            : throw new InvalidOperationException("Unsupported planning entity type.");
        return db.Set<T>().FromSqlRaw(sql, id).AsNoTracking().SingleOrDefaultAsync(token);
    }

    /// <summary>Locks only this object's material membership chain, consistent with lifecycle lock ordering.</summary>
    private async Task<(IPlanningTarget Target, Guid Parent)> LockTargetAsync(Guid tripId, string kind, Guid id, string owner, CancellationToken token)
    {
        if (kind == "trip")
        {
            var trip = await LockAsync<Trip>(id, token);
            if (trip == null || trip.Id != tripId || trip.UserId != owner) throw Unavailable();
            return (trip, tripId);
        }
        if (kind == "segment")
        {
            var segment = await LockAsync<Segment>(id, token);
            if (segment == null || segment.TripId != tripId || segment.UserId != owner) throw Unavailable();
            return (segment, tripId);
        }
        IPlanningTarget target;
        Guid regionId;
        if (kind == "place")
        {
            var place = await LockAsync<Place>(id, token);
            if (place == null || place.UserId != owner) throw Unavailable();
            target = place; regionId = place.RegionId;
        }
        else if (kind == "area")
        {
            var area = await LockAsync<Area>(id, token);
            if (area == null) throw Unavailable();
            target = area; regionId = area.RegionId;
        }
        else if (kind == "region") { target = null!; regionId = id; }
        else throw Unavailable();
        var region = await LockAsync<Region>(regionId, token);
        if (region == null || region.TripId != tripId || region.UserId != owner) throw Unavailable();
        return kind == "region" ? (region, tripId) : (target, regionId);
    }

    /// <summary>Property-only updates preserve disjoint Notes, geometry, route and freshness.</summary>
    private Task<int> WriteAsync(string kind, Guid id, PlanningAggregate? value, CancellationToken token) => kind switch
    {
        "trip" => db.Trips.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Planning, value), token),
        "region" => db.Regions.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Planning, value), token),
        "place" => db.Places.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Planning, value), token),
        "area" => db.Areas.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Planning, value), token),
        "segment" => db.Segments.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Planning, value), token),
        _ => throw Unavailable()
    };

    /// <summary>Foreign and absent graph chains are concealed identically.</summary>
    private static TripMetadataMutationRejected Unavailable() => new(404, "mutation-scope-unavailable", "Planning target not found.");
}

/// <summary>Confirmed commit acknowledgement, scoped to this attempt; never a durable retry receipt.</summary>
internal sealed record PlanningMutationResult(string Contract, bool Success, bool Applied, string TargetKind, Guid TargetId, Guid ParentId, PlanningAggregate? Planning);
