using System.Data;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>Server-owned PostgreSQL scalar compare/apply authority shared by current Web and opt-in bearer Region edits.</summary>
internal sealed class RegionConditionalMutationService(ApplicationDbContext db)
{
    /// <summary>Compares fresh originals after all waits, writes only requested scalars, and stamps the parent atomically.</summary>
    internal async Task<RegionConditionalResult> ApplyAsync(Guid tripId, Guid regionId, string userId, Stream body, bool web,
        Func<CancellationToken, Task<bool>>? revalidateBearer, CancellationToken token)
    {
        if (!await db.Regions.AsNoTracking().AnyAsync(r => r.Id == regionId && r.TripId == tripId
                && r.UserId == userId && r.Trip.UserId == userId, token))
            throw Unavailable();
        var request = await RegionConditionalRequest.ReadAsync(body, web, token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var commitStarted = false;
        try
        {
            // Region then Trip matches the existing lifecycle's child-before-parent lock order.
            // No tracked/preflight entity may supply the values used after either lock wait.
            var region = await db.Regions.FromSqlInterpolated($"SELECT * FROM public.\"Regions\" WHERE \"Id\" = {regionId} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(token);
            if (region == null || region.UserId != userId) throw Unavailable();
            if (region.TripId != tripId)
                throw new TripMetadataMutationRejected(409, "mutation-scope-stale", "The Region scope changed. Keep your draft and review its current parent.");
            var trip = await db.Trips.FromSqlInterpolated($"SELECT * FROM public.\"Trips\" WHERE \"Id\" = {tripId} AND \"UserId\" = {userId} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(token);
            if (trip == null) throw Unavailable();
            var active = await db.Users.FromSqlInterpolated($"SELECT * FROM public.\"AspNetUsers\" WHERE \"Id\" = {userId} AND \"IsActive\" FOR SHARE")
                .AsNoTracking().ToListAsync(token);
            if (active.Count == 0 || revalidateBearer != null && !await revalidateBearer(token))
                throw new TripMetadataMutationRejected(401, "mutation-authority-unavailable", "Authentication is no longer valid.");
            var reserved = string.Equals(region.Name, "Unassigned Places", StringComparison.OrdinalIgnoreCase);
            if (reserved && (web || request.Changes.Keys.Any(key => key != "notes")))
                throw new TripMetadataMutationRejected(403, "mutation-region-protected", "The reserved Region cannot be edited by this operation.");
            var current = Values(region);
            if (request.Expected.Any(field => !Equals(field.Value, current[field.Key])))
                throw new TripMetadataMutationRejected(409, "mutation-base-stale",
                    "The edited Region fields changed. Keep your draft, review current values and explicitly choose a new base before saving.");
            var applied = request.Changes.Any(field => !Equals(field.Value, field.Key == "notes" ? region.Notes : current[field.Key]));
            if (applied)
            {
                await WriteAsync(region, request, token);
                var now = DateTime.UtcNow;
                await db.Trips.Where(t => t.Id == tripId && t.UserId == userId)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.UpdatedAt, t => t.UpdatedAt > now ? t.UpdatedAt : now), token);
            }
            commitStarted = true;
            await transaction.CommitAsync(token);
            return new(region, applied);
        }
        catch (Exception original)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception rollback) { throw new AggregateException("Region edit failed and rollback also failed.", original, rollback); }
            if (!commitStarted && original is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure })
                throw new TripMetadataMutationRejected(409, "mutation-contention", "Region editing contention prevented this attempt. Review current values before saving.");
            throw;
        }
    }

    /// <summary>Never attaches an earlier entity or saves unrelated tracked work, children or order.</summary>
    private async Task WriteAsync(Region region, RegionConditionalRequest request, CancellationToken token)
    {
        await db.Regions.Where(r => r.Id == region.Id && r.TripId == region.TripId && r.UserId == region.UserId).ExecuteUpdateAsync(setters =>
        {
            foreach (var (key, value) in request.Changes)
            {
                switch (key)
                {
                    case "name":
                        region.Name = (string)value!;
                        setters.SetProperty(r => r.Name, region.Name);
                        break;
                    case "notes":
                        region.Notes = (string?)value;
                        setters.SetProperty(r => r.Notes, region.Notes);
                        break;
                    case "coverImage":
                        region.CoverImageUrl = (string?)value;
                        setters.SetProperty(r => r.CoverImageUrl, region.CoverImageUrl);
                        break;
                    case "center":
                        var center = (RegionConditionalCenter?)value;
                        region.Center = center == null ? null : new Point(center.Longitude, center.Latitude) { SRID = 4326 };
                        setters.SetProperty(r => r.Center, region.Center);
                        break;
                }
            }
        }, token);
    }

    /// <summary>Matches the published originals, with nullable normalized Notes and exact cover/center identity.</summary>
    private static Dictionary<string, object?> Values(Region region) => new()
    {
        ["name"] = region.Name, ["notes"] = RichNotes.Normalize(region.Notes), ["coverImage"] = region.CoverImageUrl,
        ["center"] = region.Center == null ? null : new RegionConditionalCenter(region.Center.Y, region.Center.X)
    };

    /// <summary>Conceals absent and foreign scopes identically.</summary>
    private static TripMetadataMutationRejected Unavailable() => new(404, "mutation-scope-unavailable", "Region not found.");
}

/// <summary>Returned only after commit; neither a retry receipt nor proof of client acknowledgement.</summary>
internal sealed record RegionConditionalResult(Region Region, bool Applied);
