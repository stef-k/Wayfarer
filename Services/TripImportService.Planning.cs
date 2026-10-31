using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Planning;

namespace Wayfarer.Services;

public partial class TripImportService
{
    /// <summary>Fences current writers and preserves same-ID/type planning through legacy native replacement.</summary>
    private async Task PreserveReplacementPlanningAsync(Trip mapped, string owner, CancellationToken token)
    {
        if (_dbContext.Database.IsRelational())
        {
            // Match lifecycle lock classes. Parent barriers fence additions; re-read all content after waits.
            await _dbContext.Segments.FromSqlInterpolated($"SELECT *, xmin FROM public.\"Segments\" WHERE \"TripId\" = {mapped.Id} ORDER BY \"Id\" FOR UPDATE").AsNoTracking().ToListAsync(token);
            await _dbContext.Places.FromSqlInterpolated($"SELECT p.* FROM public.\"Places\" p JOIN public.\"Regions\" r ON p.\"RegionId\" = r.\"Id\" WHERE r.\"TripId\" = {mapped.Id} ORDER BY p.\"Id\" FOR UPDATE OF p").AsNoTracking().ToListAsync(token);
            await _dbContext.Areas.FromSqlInterpolated($"SELECT a.* FROM public.\"Areas\" a JOIN public.\"Regions\" r ON a.\"RegionId\" = r.\"Id\" WHERE r.\"TripId\" = {mapped.Id} ORDER BY a.\"Id\" FOR UPDATE OF a").AsNoTracking().ToListAsync(token);
            await _dbContext.Regions.FromSqlInterpolated($"SELECT * FROM public.\"Regions\" WHERE \"TripId\" = {mapped.Id} ORDER BY \"Id\" FOR UPDATE").AsNoTracking().ToListAsync(token);
            await _dbContext.Trips.FromSqlInterpolated($"SELECT * FROM public.\"Trips\" WHERE \"Id\" = {mapped.Id} FOR UPDATE").AsNoTracking().ToListAsync(token);
        }
        var current = await _dbContext.Trips.AsNoTracking().Include(t => t.Regions).ThenInclude(r => r.Places)
            .Include(t => t.Regions).ThenInclude(r => r.Areas).Include(t => t.Segments)
            .AsSplitQuery().SingleOrDefaultAsync(t => t.Id == mapped.Id && t.UserId == owner, token);
        if (current == null) throw new InvalidOperationException("Trip not found or not yours for upsert.");
        if (current.Regions.Any(r => r.UserId != owner || r.Places.Any(p => p.UserId != owner))
            || current.Segments.Any(s => s.UserId != owner)) throw new TripImportValidationException("The current graph requires reconciliation.");
        var active = _dbContext.Database.IsRelational()
            ? (await _dbContext.Users.FromSqlInterpolated($"SELECT * FROM public.\"AspNetUsers\" WHERE \"Id\" = {owner} AND \"IsActive\" FOR SHARE").AsNoTracking().ToListAsync(token)).Count > 0
            : await _dbContext.Users.AsNoTracking().AnyAsync(u => u.Id == owner && u.IsActive, token);
        if (!active) throw new InvalidOperationException("Import authority is no longer valid.");
        Preserve(current.Regions, mapped.Regions, r => r.Id);
        Preserve(current.Regions.SelectMany(r => r.Places), mapped.Regions.SelectMany(r => r.Places), p => p.Id);
        Preserve(current.Regions.SelectMany(r => r.Areas), mapped.Regions.SelectMany(r => r.Areas), a => a.Id);
        Preserve(current.Segments, mapped.Segments, s => s.Id);
        // The existing root is updated property-by-property; omitted root planning remains untouched.
    }

    /// <summary>Identity/type is the only retention key. Annotated loss rejects until WB2 supplies bound review choices.</summary>
    private static void Preserve<T>(IEnumerable<T> previous, IEnumerable<T> next, Func<T, Guid> id) where T : IPlanningTarget
    {
        var targets = next.ToDictionary(id);
        foreach (var source in previous)
        {
            if (source.Planning == null) continue;
            if (!targets.TryGetValue(id(source), out var target)) throw new TripImportPlanningLossException();
            target.Planning = source.Planning;
        }
    }
}

/// <summary>Known rollback with a safe owner-only explanation; no values or counts leak into the error.</summary>
public sealed class TripImportPlanningLossException() : Exception("This update would remove objects with private planning. Keep the original Trip or import as a new Trip. Reviewed planning removal belongs to the personal import workflow.");
