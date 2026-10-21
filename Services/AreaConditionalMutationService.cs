using System.Data;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>Single PostgreSQL compare/apply authority for current Web Area edits and opt-in bearer Notes.</summary>
internal sealed class AreaConditionalMutationService(ApplicationDbContext db)
{
    /// <summary>Locks child before parents, compares fresh scope/originals and commits only intended columns plus Trip freshness.</summary>
    internal async Task<AreaConditionalResult> ApplyAsync(Guid tripId, Guid areaId, string userId, Stream body, bool web,
        Func<CancellationToken, Task<bool>>? revalidateBearer, CancellationToken token)
    {
        if (!await db.Areas.AsNoTracking().AnyAsync(a => a.Id == areaId && a.Region.TripId == tripId
                && a.Region.UserId == userId && a.Region.Trip.UserId == userId, token)) throw Unavailable();
        var request = await AreaConditionalRequest.ReadAsync(body, web, token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var commitStarted = false;
        try
        {
            // #848 Region keep/delete locks Areas before Regions/Trip. Never reuse tracked pre-wait values.
            var area = await db.Areas.FromSqlInterpolated($"SELECT * FROM public.\"Areas\" WHERE \"Id\" = {areaId} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(token);
            if (area == null) throw Unavailable();
            var region = await db.Regions.FromSqlInterpolated($"SELECT * FROM public.\"Regions\" WHERE \"Id\" = {area.RegionId} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(token);
            if (region == null || region.UserId != userId) throw Unavailable();
            var trip = await db.Trips.FromSqlInterpolated($"SELECT * FROM public.\"Trips\" WHERE \"Id\" = {region.TripId} AND \"UserId\" = {userId} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(token);
            if (trip == null) throw Unavailable();
            var active = await db.Users.FromSqlInterpolated($"SELECT * FROM public.\"AspNetUsers\" WHERE \"Id\" = {userId} AND \"IsActive\" FOR SHARE")
                .AsNoTracking().ToListAsync(token);
            if (active.Count == 0 || revalidateBearer != null && !await revalidateBearer(token))
                throw new TripMetadataMutationRejected(401, "mutation-authority-unavailable", "Authentication is no longer valid.");
            if (region.TripId != tripId || area.RegionId != request.RegionId)
                throw new TripMetadataMutationRejected(409, "mutation-scope-stale", "The Area parent changed. Keep your draft and review its current Region.");
            if (request.Expected.Any(field => !Equals(field.Value, Current(area, field.Key))))
                throw new TripMetadataMutationRejected(409, "mutation-base-stale",
                    "The edited Area fields changed. Keep your draft, review current values and explicitly choose a new base before saving.");
            var applied = request.Changes.Any(field => field.Key == "geometry"
                ? AreaGeometryIdentity.For((Polygon)field.Value!) != AreaGeometryIdentity.For(area.Geometry)
                : !Equals(field.Value, field.Key == "notes" ? area.Notes : Current(area, field.Key)));
            if (applied)
            {
                await WriteAsync(area, request, token);
                var now = DateTime.UtcNow;
                await db.Trips.Where(t => t.Id == tripId && t.UserId == userId)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.UpdatedAt, t => t.UpdatedAt > now ? t.UpdatedAt : now), token);
            }
            commitStarted = true;
            await transaction.CommitAsync(token);
            return new(area, applied);
        }
        catch (Exception original)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception rollback) { throw new AggregateException("Area edit failed and rollback also failed.", original, rollback); }
            if (!commitStarted && original is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure })
                throw new TripMetadataMutationRejected(409, "mutation-contention", "Area editing contention prevented this attempt. Review current values before saving.");
            throw;
        }
    }

    /// <summary>Original Notes use the shared publication canonicalization, preserving null separately from empty.</summary>
    private static object? Current(Area area, string key) => key switch
    {
        "name" => area.Name, "notes" => RichNotes.Normalize(area.Notes), "fillHex" => area.FillHex,
        "geometry" => AreaGeometryIdentity.For(area.Geometry), _ => throw new InvalidOperationException("Unsupported Area field.")
    };

    /// <summary>Uses explicit property setters without attaching stale entities or saving unrelated tracked work.</summary>
    private async Task WriteAsync(Area area, AreaConditionalRequest request, CancellationToken token)
    {
        await db.Areas.Where(a => a.Id == area.Id && a.RegionId == area.RegionId).ExecuteUpdateAsync(setters =>
        {
            foreach (var (key, value) in request.Changes)
            {
                switch (key)
                {
                    case "name":
                        area.Name = (string)value!;
                        setters.SetProperty(a => a.Name, area.Name);
                        break;
                    case "notes":
                        area.Notes = (string?)value;
                        setters.SetProperty(a => a.Notes, area.Notes);
                        break;
                    case "fillHex":
                        area.FillHex = (string)value!;
                        setters.SetProperty(a => a.FillHex, area.FillHex);
                        break;
                    case "geometry":
                        area.Geometry = (Polygon)value!;
                        setters.SetProperty(a => a.Geometry, area.Geometry);
                        break;
                }
            }
        }, token);
    }

    /// <summary>Conceals missing and foreign parent chains identically.</summary>
    private static TripMetadataMutationRejected Unavailable() => new(404, "mutation-scope-unavailable", "Area not found.");
}

/// <summary>Returned only after commit; does not imply delivery of the HTTP reply or provide an idempotency receipt.</summary>
internal sealed record AreaConditionalResult(Area Area, bool Applied);
