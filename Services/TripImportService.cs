using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Models;
using Wayfarer.Parsers;

namespace Wayfarer.Services;

public partial class TripImportService : ITripImportService
{
    readonly ApplicationDbContext _dbContext;
    readonly ILogger<TripImportService> _log;
    readonly ITripImportTagReconciler _tagReconciler;

    /// <summary>Creates an importer for focused tests without changing production DI composition.</summary>
    internal TripImportService(ApplicationDbContext dbContext, ILogger<TripImportService> log)
        : this(dbContext, log, new TripImportTagReconciler(dbContext, NullLogger<TripImportTagReconciler>.Instance))
    {
    }

    /// <summary>Creates an importer with the shared tag persistence boundary.</summary>
    public TripImportService(
        ApplicationDbContext dbContext,
        ILogger<TripImportService> log,
        ITripImportTagReconciler tagReconciler)
    {
        _dbContext = dbContext;
        _log = log;
        _tagReconciler = tagReconciler;
    }

    /// <summary>Classifies one hardened document and dispatches to isolated native or generic persistence.</summary>
    public async Task<TripImportResult> ImportWayfarerKmlAsync(
        Stream kmlStream,
        string userId,
        TripImportMode mode = TripImportMode.Auto,
        CancellationToken cancellationToken = default)
    {
        var classification = await WayfarerKmlParser.ClassifyAndParseAsync(kmlStream, cancellationToken);
        if (classification.Document is not null)
            return new(await ImportNativeAsync(classification.Document, userId, mode, cancellationToken), []);
        try
        {
            return await ImportGenericAsync(classification.Source, userId, mode, cancellationToken);
        }
        catch
        {
            _dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>Coordinates one complete native create or authoritative replacement transaction.</summary>
    private async Task<Guid> ImportNativeAsync(
        WayfarerKmlDocument source,
        string userId,
        TripImportMode mode,
        CancellationToken cancellationToken)
    {
        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            var existing = await _dbContext.Trips.AsNoTracking()
                .SingleOrDefaultAsync(trip => trip.Id == source.TripId, cancellationToken);
            var owned = existing?.UserId == userId;
            if (mode == TripImportMode.Auto && owned) throw new TripDuplicateException(source.TripId);
            if (mode == TripImportMode.Upsert && !owned)
                throw new InvalidOperationException("Trip not found or not yours for upsert.");

            var profiles = await _dbContext.Set<TransportProfile>().AsNoTracking()
                .ToDictionaryAsync(profile => profile.Key, cancellationToken);
            var createNew = mode != TripImportMode.Upsert;
            var mapped = WayfarerKmlAggregateMapper.Map(
                source, userId, profiles, remapIdentities: createNew,
                targetTripId: createNew ? null : source.TripId);
            if (createNew) mapped.Name = $"{mapped.Name} (Imported)";

            var reconciledTags = await _tagReconciler.ReconcileAsync(source.Tags, cancellationToken);
            if (createNew)
            {
                foreach (var tag in reconciledTags) mapped.Tags.Add(tag);
                _dbContext.Trips.Add(mapped);
            }
            else
            {
                await ReplaceNativeChildrenAsync(mapped, cancellationToken);
                var target = await _dbContext.Trips.Include(trip => trip.Tags)
                    .SingleAsync(trip => trip.Id == source.TripId && trip.UserId == userId, cancellationToken);
                target.Name = mapped.Name;
                target.Notes = mapped.Notes;
                target.CoverImageUrl = mapped.CoverImageUrl;
                target.CenterLat = mapped.CenterLat;
                target.CenterLon = mapped.CenterLon;
                target.Zoom = mapped.Zoom;
                target.UpdatedAt = DateTime.UtcNow;
                target.Tags.Clear();
                foreach (var tag in reconciledTags) target.Tags.Add(tag);
                _dbContext.Regions.AddRange(mapped.Regions);
                _dbContext.Segments.AddRange(mapped.Segments);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            _dbContext.ChangeTracker.Clear();
            await SegmentMeasurementWriterReconciler.ReconcileTripAsync(
                _dbContext, mapped.Id, allowUnavailableAutomatic: true, cancellationToken);
            await ValidateCompatibilityMeasurementsAsync(source, mapped, cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            _dbContext.ChangeTracker.Clear();
            return mapped.Id;
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            _dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>Deletes every imported child set so upsert can install the authoritative replacement.</summary>
    private async Task ReplaceNativeChildrenAsync(Trip mapped, CancellationToken cancellationToken)
    {
        if (_dbContext.Database.IsRelational())
        {
            await _dbContext.Segments.Where(segment => segment.TripId == mapped.Id).ExecuteDeleteAsync(cancellationToken);
            await _dbContext.Areas.Where(area => area.Region.TripId == mapped.Id).ExecuteDeleteAsync(cancellationToken);
            await _dbContext.Places.Where(place => place.Region.TripId == mapped.Id).ExecuteDeleteAsync(cancellationToken);
            await _dbContext.Regions.Where(region => region.TripId == mapped.Id).ExecuteDeleteAsync(cancellationToken);
            _dbContext.ChangeTracker.Clear();
            return;
        }
        var target = await _dbContext.Trips.Include(trip => trip.Regions).ThenInclude(region => region.Places)
            .Include(trip => trip.Regions).ThenInclude(region => region.Areas)
            .Include(trip => trip.Segments).ThenInclude(segment => segment.Waypoints)
            .SingleAsync(trip => trip.Id == mapped.Id, cancellationToken);
        _dbContext.Segments.RemoveRange(target.Segments);
        _dbContext.Regions.RemoveRange(target.Regions);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _dbContext.ChangeTracker.Clear();
    }

    /// <summary>Checks serialized compatibility measurements after canonical reconciliation.</summary>
    private async Task ValidateCompatibilityMeasurementsAsync(
        WayfarerKmlDocument source,
        Trip mapped,
        CancellationToken cancellationToken)
    {
        var persisted = await _dbContext.Segments.AsNoTracking().Where(segment => segment.TripId == mapped.Id)
            .ToDictionaryAsync(segment => segment.Id, cancellationToken);
        if (persisted.Count != source.Segments.Count || mapped.Segments.Count != source.Segments.Count)
            throw new TripImportValidationException("Imported Segment count changed during reconciliation.");
        for (var index = 0; index < source.Segments.Count; index++)
        {
            var expected = source.Segments[index];
            if (!persisted.TryGetValue(mapped.Segments.ElementAt(index).Id, out var actual))
                throw new TripImportValidationException("An imported Segment identity changed during reconciliation.");
            if (expected.DistanceKm.HasValue && actual.EstimatedDistanceKm != expected.DistanceKm)
                throw new TripImportValidationException("Distance compatibility metadata does not match canonical reconciliation.");
            if (expected.DurationSource == EstimatedDurationSource.Manual
                && actual.EstimatedDuration?.TotalSeconds != expected.DurationSeconds)
                throw new TripImportValidationException("Manual duration did not reconcile exactly.");
            if (expected.DurationSource == EstimatedDurationSource.Automatic && expected.DurationSeconds.HasValue
                && actual.EstimatedDuration?.TotalSeconds != expected.DurationSeconds)
                throw new TripImportValidationException("Automatic duration compatibility metadata does not match canonical reconciliation.");
        }
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
        parsed.Regions ??= new List<Region>();      
        parsed.Segments??= new List<Segment>();

        /* 2 ── regions ------------------------------------------------------ */
        foreach (var r in parsed.Regions ?? Enumerable.Empty<Region>())
        {
            var newRegId = Guid.NewGuid();
            regionMap[r.Id] = newRegId;

            r.Id = newRegId;
            r.TripId = parsed.Id;
            r.UserId  = userId;

            /* 2a ── places --------------------------------------------------- */
            r.Places ??= new List<Place>();
            foreach (var p in r.Places ?? Enumerable.Empty<Place>())
            {
                var newPlaceId = Guid.NewGuid();
                placeMap[p.Id] = newPlaceId;

                p.Id = newPlaceId;
                p.RegionId = newRegId;
                p.UserId  = userId;
            }
            
            /* 2b ── places --------------------------------------------------- */
            r.Areas ??= new List<Area>();
            foreach (var a in r.Areas)
            {
                a.Id = Guid.NewGuid();       
                a.RegionId = newRegId;      
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
