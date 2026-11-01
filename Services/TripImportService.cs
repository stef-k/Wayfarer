using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Models;
using Wayfarer.Parsers;

namespace Wayfarer.Services;

public partial class TripImportService : ITripImportService
{
    readonly ApplicationDbContext _dbContext;
    readonly ILogger<TripImportService> _log;
    readonly ITripImportTagReconciler _tagReconciler;
    readonly NativeImportReviewProtection _review;

    /// <summary>Creates an importer for focused tests without changing production DI composition.</summary>
    internal TripImportService(ApplicationDbContext dbContext, ILogger<TripImportService> log)
        : this(dbContext, log, new TripImportTagReconciler(dbContext, NullLogger<TripImportTagReconciler>.Instance))
    {
    }

    /// <summary>Creates an importer with the shared tag persistence boundary.</summary>
    internal TripImportService(ApplicationDbContext dbContext, ILogger<TripImportService> log, ITripImportTagReconciler tagReconciler)
        : this(dbContext, log, tagReconciler, new EphemeralDataProtectionProvider()) { }

    /// <summary>Production review permits use the existing persistent application Data Protection authority.</summary>
    public TripImportService(
        ApplicationDbContext dbContext,
        ILogger<TripImportService> log,
        ITripImportTagReconciler tagReconciler,
        IDataProtectionProvider protection)
    {
        _dbContext = dbContext;
        _log = log;
        _tagReconciler = tagReconciler;
        _review = new(protection);
    }

    /// <summary>Classifies one hardened document and dispatches to isolated native or generic persistence.</summary>
    public async Task<TripImportResult> ImportWayfarerKmlAsync(
        Stream kmlStream,
        string userId,
        TripImportMode mode = TripImportMode.Auto,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode)) throw new TripImportValidationException("Unsupported import mode.");
        var (classification, digest) = await ReadInputAsync(kmlStream, cancellationToken);
        if (classification.Document is not null)
            return new(await ImportNativeAsync(classification.Document, userId, mode, digest, null, cancellationToken), []);
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
        string digest,
        NativeImportApproval? approval,
        CancellationToken cancellationToken)
    {
        if (source.Regions.Count(region => region.Name == "Unassigned Places") > 1)
            throw new TripImportValidationException("Duplicate Unassigned Places Regions require reconciliation.");
        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var committing = false;
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
            TripRichNotes.NormalizeDestination(mapped);
            if (createNew && source.Version != 4) mapped.Name = $"{mapped.Name} (Imported)";

            if (!createNew)
            {
                var current = await LockReplacementAsync(mapped.Id, userId, cancellationToken);
                await ValidateReplacementIdentitiesAsync(mapped, cancellationToken);
                _review.Apply(current, mapped, NativeImportReviewProtection.Binding(current, userId, digest), approval);
            }
            else await RequireActiveImportOwnerAsync(userId, cancellationToken);
            var reconciledTags = await _tagReconciler.ReconcileAsync(source.Tags, cancellationToken);
            if (createNew)
            {
                foreach (var tag in reconciledTags) mapped.Tags.Add(tag);
                _dbContext.Trips.Add(mapped);
            }
            else
            {
                await ReplaceNativeChildrenAsync(mapped, cancellationToken);
                // Replacement clears the tracker; use these canonical tags when loading existing joins.
                _dbContext.AttachRange(reconciledTags);
                var target = await _dbContext.Trips.Include(trip => trip.Tags)
                    .SingleAsync(trip => trip.Id == source.TripId && trip.UserId == userId, cancellationToken);
                target.Name = mapped.Name;
                target.Notes = mapped.Notes;
                target.CoverImageUrl = mapped.CoverImageUrl;
                target.CenterLat = mapped.CenterLat;
                target.CenterLon = mapped.CenterLon;
                target.Zoom = mapped.Zoom;
                target.Planning = mapped.Planning;
                target.UpdatedAt = DateTime.UtcNow;
                target.Tags.Clear();
                foreach (var tag in reconciledTags) target.Tags.Add(tag);
                _dbContext.Regions.AddRange(mapped.Regions);
                _dbContext.Segments.AddRange(mapped.Segments);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            _dbContext.ChangeTracker.Clear();
            await SegmentMeasurementWriterReconciler.ReconcileTripAsync(
                _dbContext, mapped.Id, allowUnavailableAutomatic: true, cancellationToken,
                preserveSavedMeasurements: source.Version is 3 or 4);
            await ValidateCompatibilityMeasurementsAsync(source, mapped, cancellationToken);
            if (transaction is not null)
            {
                committing = true;
                await transaction.CommitAsync(cancellationToken);
            }
            _dbContext.ChangeTracker.Clear();
            return mapped.Id;
        }
        catch (Exception primary)
        {
            _dbContext.ChangeTracker.Clear();
            if (transaction is not null)
            {
                try { await transaction.RollbackAsync(CancellationToken.None); }
                catch (Exception rollback) { throw new AggregateException("Native import failed and rollback also failed.", primary, rollback); }
            }
            if (committing) throw new NativeImportOutcomeUnknownException();
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

    /// <summary>Checks v1/v2 compatibility reconciliation or exact v3 saved-value retention before commit.</summary>
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
            if ((source.Version is 3 or 4 || expected.DistanceKm.HasValue) && actual.EstimatedDistanceKm != expected.DistanceKm)
                throw new TripImportValidationException("Distance compatibility metadata does not match canonical reconciliation.");
            if (expected.DurationSource == EstimatedDurationSource.Manual
                && actual.EstimatedDuration != expected.Duration)
                throw new TripImportValidationException("Manual duration did not reconcile exactly.");
            if (expected.DurationSource == EstimatedDurationSource.Automatic && (source.Version is 3 or 4 || expected.Duration.HasValue)
                && actual.EstimatedDuration != expected.Duration)
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
