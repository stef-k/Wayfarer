using System.Data;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>Describes an eligibility or success outcome from one authoritative Trip clone attempt.</summary>
public sealed record TripCloneResult(
    TripCloneStatus Status,
    Guid? ClonedTripId = null,
    string? SourceTripName = null,
    bool RequiresImageWarmup = false, bool MediaOmitted = false);

/// <summary>Identifies path-neutral Trip clone outcomes translated by each controller.</summary>
public enum TripCloneStatus
{
    /// <summary>The source Trip does not exist.</summary>
    NotFound,
    /// <summary>The source Trip is not public.</summary>
    NotPublic,
    /// <summary>The destination user already owns the source Trip.</summary>
    AlreadyOwned,
    /// <summary>The complete private clone committed successfully.</summary>
    Succeeded
}

/// <summary>Loads, validates, constructs, reconciles, and atomically persists complete Trip clones.</summary>
public sealed partial class TripCloneCoordinator(ApplicationDbContext dbContext)
{
    /// <summary>Clones one eligible public Trip into the destination user's account.</summary>
    public async Task<TripCloneResult> CloneAsync(
        Guid sourceTripId,
        string destinationUserId,
        CancellationToken cancellationToken = default)
    {
        var sourceOwner = await dbContext.Trips.AsNoTracking().Where(t => t.Id == sourceTripId).Select(t => t.UserId).SingleOrDefaultAsync(cancellationToken);
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            : null;
        try
        {
            if (transaction != null && sourceOwner != null)
                foreach (var owner in new[] { sourceOwner, destinationUserId }.Distinct().Order(StringComparer.Ordinal))
                    await LocationDeletionFence.AcquireAsync(dbContext, owner, cancellationToken);
            if (transaction != null)
                foreach (var owner in new[] { sourceOwner, destinationUserId }.OfType<string>().Distinct().Order(StringComparer.Ordinal))
                    await dbContext.Users.FromSqlInterpolated($"SELECT * FROM public.\"AspNetUsers\" WHERE \"Id\" = {owner} FOR SHARE")
                        .AsNoTracking().ToListAsync(cancellationToken);
            if (transaction != null)
            {
                var profiles = await dbContext.Segments.AsNoTracking().Where(s => s.TripId == sourceTripId && s.TransportProfileId != null)
                    .Select(s => s.TransportProfileId!.Value).Distinct().OrderBy(id => id).ToArrayAsync(cancellationToken);
                await SegmentRouteReconciler.LockProfilesAsync(dbContext, profiles, cancellationToken);
            }
            if (transaction != null)
                await dbContext.Trips.FromSqlInterpolated($"SELECT * FROM public.\"Trips\" WHERE \"Id\" = {sourceTripId} FOR SHARE")
                    .AsNoTracking().ToListAsync(cancellationToken);
            var source = await LoadSourceAsync(sourceTripId, cancellationToken);
            if (source == null) return new(TripCloneStatus.NotFound);
            if (!source.IsPublic) return new(TripCloneStatus.NotPublic);
            if (source.UserId == destinationUserId) return new(TripCloneStatus.AlreadyOwned);
            if (!TripReusePolicy.GrantsReuse(source)
                || !await dbContext.Users.AnyAsync(u => u.Id == source.UserId && u.IsActive, cancellationToken)
                || !await dbContext.Users.AnyAsync(u => u.Id == destinationUserId && u.IsActive, cancellationToken))
                return new(TripCloneStatus.NotPublic);

            if (source.Regions.Count(region => region.Name == "Unassigned Places") > 1)
                throw new InvalidOperationException("Duplicate Unassigned Places Regions require reconciliation.");

            var clone = await ConstructCloneAsync(source, destinationUserId, cancellationToken);
            var mediaOmitted = TripCopyMedia.OmitUnqualified(clone);
            TripRichNotes.NormalizeDestination(clone);
            await PersistConstructedAsync(clone, cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);

            var requiresWarmup = !string.IsNullOrWhiteSpace(clone.CoverImageUrl)
                || HtmlHelpers.ExtractExternalImageUrls(clone.Notes).Any();
            return new(TripCloneStatus.Succeeded, clone.Id, source.Name, requiresWarmup, mediaOmitted);
        }
        catch (Exception cloneFailure)
        {
            // A controller may audit with this context after rollback; never leave the failed clone staged.
            dbContext.ChangeTracker.Clear();
            if (transaction == null) throw;
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException("Trip cloning failed and its transaction rollback also failed.",
                    cloneFailure, rollbackFailure);
            }
            throw;
        }
    }

    /// <summary>Explicitly loads every clone-owned aggregate child inside the coordinator snapshot.</summary>
    internal Task<Trip?> LoadSourceAsync(Guid sourceTripId, CancellationToken cancellationToken) =>
        dbContext.Trips.AsNoTracking()
            .Include(trip => trip.User)
            .Include(trip => trip.Tags)
            .Include(trip => trip.Regions).ThenInclude(region => region.Places)
            .Include(trip => trip.Regions).ThenInclude(region => region.Areas)
            .Include(trip => trip.Segments).ThenInclude(segment => segment.Waypoints)
            .Include(trip => trip.Segments).ThenInclude(segment => segment.TransportProfile)
            .AsSplitQuery()
            .SingleOrDefaultAsync(trip => trip.Id == sourceTripId, cancellationToken);

}
