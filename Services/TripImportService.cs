using System.Text;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Parsers;

namespace Wayfarer.Services;

public class TripImportService : ITripImportService
{
    readonly ApplicationDbContext _dbContext;
    readonly ILogger<TripImportService> _log;

    public TripImportService(ApplicationDbContext dbContext, ILogger<TripImportService> log)
    {
        _dbContext = dbContext;
        _log = log;
    }

    public async Task<Guid> ImportWayfarerKmlAsync(
        Stream kmlStream,
        string userId,
        TripImportMode mode = TripImportMode.Auto)
    {
        /* 1 ── detect which flavour of KML (Wayfarer or Google MyMaps ------------------------------ */
        Trip parsed;
        using var mem = new MemoryStream();
        await kmlStream.CopyToAsync(mem);
        var xmlText = Encoding.UTF8.GetString(mem.ToArray());
        mem.Position = 0;

        bool isWayfarer = xmlText.Contains("wf:TripId") || xmlText.Contains("wf:PlaceId");
        parsed = isWayfarer
            ? WayfarerKmlParser.Parse(mem)
            : GoogleMyMapsKmlParser.Parse(mem, userId);

        /* 2- decide target trip ----------------------------------------- */
        var dbTrip = await _dbContext.Trips
            .Include(t => t.Regions).ThenInclude(r => r.Places)
            .Include(t => t.Segments)
            .FirstOrDefaultAsync(t => t.Id == parsed.Id);

        bool owned = dbTrip?.UserId == userId;

        if (mode == TripImportMode.Auto && owned)
        {
            // Let the controller ask the user what to do
            throw new TripDuplicateException(dbTrip!.Id);
        }

        Trip target;

        switch (mode)
        {
            case TripImportMode.Upsert:
                if (!owned)
                    throw new InvalidOperationException("Trip not found or not yours for upsert.");
                target = dbTrip!;
                break;

            case TripImportMode.CreateNew:
                parsed.Id = Guid.NewGuid();
                target = CreateNewShell(parsed, userId);
                _dbContext.Trips.Add(target);
                target.Name = $"{target.Name} (Imported)"; 
                break;

            case TripImportMode.Auto:
            default:
                target = owned
                    ? dbTrip!                               // upsert path → keep name
                    : CreateNewShell(parsed, userId);       // clone
                if (!owned)
                {
                    _dbContext.Trips.Add(target);
                    target.Name = $"{target.Name} (Imported)";   // ★ tag once
                }
                break;
        }

        /* 3 sync scalar properties ------------------------------------ */
        if (mode == TripImportMode.Upsert || owned)            // keep existing name only when upserting
            target.Name = parsed.Name;
        target.Notes = parsed.Notes;
        target.CenterLat = parsed.CenterLat;
        target.CenterLon = parsed.CenterLon;
        target.Zoom = parsed.Zoom;
        target.UpdatedAt = DateTime.UtcNow;

        /* 4 sync regions  segments ----------------------------------- */
        SyncCollection(parsed.Regions, target.Regions, (p, d) => p.Id == d.Id);
        SyncCollection(parsed.Segments, target.Segments, (p, d) => p.Id == d.Id);

        /* 5 sync places inside each region ---------------------------- */
        foreach (var pReg in parsed.Regions)
        {
            var tReg = target.Regions.First(r => r.Id == pReg.Id);
            SyncCollection(pReg.Places ?? Enumerable.Empty<Place>(),
                tReg.Places,
                (p, d) => p.Id == d.Id);
        }

        await _dbContext.SaveChangesAsync();
        return target.Id;
    }

    /* ---------- helpers ------------------------------------------------- */
    static Trip CreateNewShell(Trip parsed, string userId)
    {
        /* 0 ── remap dictionaries ------------------------------------------ */
        var regionMap = new Dictionary<Guid, Guid>();
        var placeMap = new Dictionary<Guid, Guid>();

        /* 1 ── trip --------------------------------------------------------- */
        parsed.Id = Guid.NewGuid();
        parsed.UserId = userId;

        /* 2 ── regions ------------------------------------------------------ */
        foreach (var r in parsed.Regions ?? Enumerable.Empty<Region>())
        {
            var newRegId = Guid.NewGuid();
            regionMap[r.Id] = newRegId;

            r.Id = newRegId;
            r.TripId = parsed.Id;
            r.UserId  = userId;

            /* 2a ── places --------------------------------------------------- */
            foreach (var p in r.Places ?? Enumerable.Empty<Place>())
            {
                var newPlaceId = Guid.NewGuid();
                placeMap[p.Id] = newPlaceId;

                p.Id = newPlaceId;
                p.RegionId = newRegId;
                p.UserId  = userId;
            }
        }

        /* 3 ── segments ----------------------------------------------------- */
        foreach (var s in parsed.Segments ?? Enumerable.Empty<Segment>())
        {
            s.Id = Guid.NewGuid();
            s.TripId = parsed.Id;
            s.UserId  = userId;

            if (s.FromPlaceId != null && placeMap.TryGetValue(s.FromPlaceId.Value, out var newFrom))
                s.FromPlaceId = newFrom;
            if (s.ToPlaceId != null && placeMap.TryGetValue(s.ToPlaceId.Value, out var newTo))
                s.ToPlaceId = newTo;
        }

        return parsed;
    }


    /* generic upsert for any child set */
    void SyncCollection<T>(
        IEnumerable<T> parsed,
        ICollection<T> dbSet,
        Func<T, T, bool> match) where T : class
    {
        /* up-date existing + insert new */
        foreach (var p in parsed)
        {
            var d = dbSet.FirstOrDefault(x => match(p, x));
            if (d == null)
                dbSet.Add(p);
            else
                _dbContext.Entry(d).CurrentValues.SetValues(p);
        }

        /* optional: delete removed items
        var toRemove = dbSet.Where(d => !parsed.Any(p => match(p, d)))
                            .ToList();
        foreach (var item in toRemove) dbSet.Remove(item);
        */
    }
}