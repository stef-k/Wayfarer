using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>Single PostgreSQL compare/apply authority for paired Web and bearer Place scalar edits.</summary>
internal sealed class PlaceConditionalMutationService(ApplicationDbContext db, IWebHostEnvironment environment, IIconColorProvider colors)
{
    /// <summary>Locks child before parents, compares fresh scope/originals and commits only intended columns plus Trip freshness.</summary>
    internal async Task<PlaceConditionalResult> ApplyAsync(Guid tripId, Guid placeId, string userId, Stream body, bool web,
        Func<CancellationToken, Task<bool>>? revalidateBearer, CancellationToken token)
    {
        if (!await db.Places.AsNoTracking().AnyAsync(a => a.Id == placeId && a.Region.TripId == tripId
                && a.UserId == userId && a.Region.UserId == userId && a.Region.Trip.UserId == userId, token)) throw Unavailable();
        var request = await PlaceConditionalRequest.ReadAsync(body, web, IconNames(),
            (colors.GetAvailableColors()?.Backgrounds ?? []).ToHashSet(StringComparer.Ordinal), token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var commitStarted = false;
        try
        {
            // Lifecycle/Segment writers lock profiles, Segments, Places, Regions, then stamp Trip.
            // This operation takes only their Place/Region/Trip suffix and never requests an earlier lock.
            var place = await db.Places.FromSqlInterpolated($"SELECT * FROM public.\"Places\" WHERE \"Id\" = {placeId} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(token);
            if (place == null || place.UserId != userId) throw Unavailable();
            var region = await db.Regions.FromSqlInterpolated($"SELECT * FROM public.\"Regions\" WHERE \"Id\" = {place.RegionId} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(token);
            if (region == null || region.UserId != userId) throw Unavailable();
            var trip = await db.Trips.FromSqlInterpolated($"SELECT * FROM public.\"Trips\" WHERE \"Id\" = {region.TripId} AND \"UserId\" = {userId} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(token);
            if (trip == null) throw Unavailable();
            var active = await db.Users.FromSqlInterpolated($"SELECT * FROM public.\"AspNetUsers\" WHERE \"Id\" = {userId} AND \"IsActive\" FOR SHARE")
                .AsNoTracking().ToListAsync(token);
            if (active.Count == 0 || revalidateBearer != null && !await revalidateBearer(token))
                throw new TripMetadataMutationRejected(401, "mutation-authority-unavailable", "Authentication is no longer valid.");
            if (region.TripId != tripId || place.RegionId != request.RegionId)
                throw new TripMetadataMutationRejected(409, "mutation-scope-stale", "The Place parent changed. Keep your draft and review its current Region.");
            if (request.Expected.Any(field => !Equals(field.Value, Current(place, field.Key))))
                throw new TripMetadataMutationRejected(409, "mutation-base-stale",
                    "The edited Place fields changed. Keep your draft, review current values and explicitly choose a new base before saving.");
            var applied = request.Changes.Any(field => !Equals(field.Value, field.Key == "notes" ? place.Notes : Current(place, field.Key)));
            if (applied)
            {
                await WriteAsync(place, request, token);
                var now = DateTime.UtcNow;
                await db.Trips.Where(t => t.Id == tripId && t.UserId == userId)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.UpdatedAt, t => t.UpdatedAt > now ? t.UpdatedAt : now), token);
            }
            commitStarted = true;
            await transaction.CommitAsync(token);
            return new(place, applied);
        }
        catch (Exception original)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception rollback) { throw new AggregateException("Place edit failed and rollback also failed.", original, rollback); }
            if (!commitStarted && original is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure })
                throw new TripMetadataMutationRejected(409, "mutation-contention", "Place editing contention prevented this attempt. Review current values before saving.");
            throw;
        }
    }

    /// <summary>Original Notes use the shared publication canonicalization, preserving null separately from empty.</summary>
    private static object? Current(Place place, string key) => key switch
    {
        "name" => place.Name, "notes" => RichNotes.Normalize(place.Notes), "iconName" => place.IconName, "markerColor" => place.MarkerColor, _ => throw new InvalidOperationException("Unsupported Place field.")
    };

    /// <summary>Uses explicit property setters without attaching stale entities or saving unrelated tracked work.</summary>
    private async Task WriteAsync(Place place, PlaceConditionalRequest request, CancellationToken token)
    {
        await db.Places.Where(a => a.Id == place.Id && a.RegionId == place.RegionId).ExecuteUpdateAsync(setters =>
        {
            foreach (var (key, value) in request.Changes)
            {
                switch (key)
                {
                    case "name":
                        place.Name = (string)value!;
                        setters.SetProperty(a => a.Name, place.Name);
                        break;
                    case "notes":
                        place.Notes = (string?)value;
                        setters.SetProperty(a => a.Notes, place.Notes);
                        break;
                    case "iconName":
                        place.IconName = value;
                        setters.SetProperty(a => a.IconName, place.IconName);
                        break;
                    case "markerColor":
                        place.MarkerColor = value;
                        setters.SetProperty(a => a.MarkerColor, place.MarkerColor);
                        break;
                }
            }
        }, token);
    }

    /// <summary>Uses the same shipped SVG catalog as the complete Web editor writer.</summary>
    private IReadOnlySet<string> IconNames()
    {
        var directory = Path.Combine(environment.WebRootPath, "icons", "wayfarer-map-icons", "dist", "marker");
        return (Directory.Exists(directory) ? Directory.GetFiles(directory, "*.svg").Select(Path.GetFileNameWithoutExtension).OfType<string>() : [])
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Conceals missing and foreign parent chains identically.</summary>
    private static TripMetadataMutationRejected Unavailable() => new(404, "mutation-scope-unavailable", "Place not found.");
}

/// <summary>Returned only after commit; does not imply delivery of the HTTP reply or provide an idempotency receipt.</summary>
internal sealed record PlaceConditionalResult(Place Place, bool Applied);
