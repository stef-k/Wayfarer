using Microsoft.EntityFrameworkCore;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Models.Planning;
using Wayfarer.Services.TripMetadataReceipts;

namespace Wayfarer.Services;

public sealed partial class PlaceRegionLifecycleService
{
    /// <summary>Supplies an atomic projection of the reviewed Region deletion and its dependent effects.</summary>
    internal async Task<TripStructureResult> DeleteRegionWithReceiptAsync(TripStructureWrite write)
    {
        try
        {
            using var body = TripStructureScalars.Body(write.Payload);
            var choices = await RegionDeletionRequestParser.ParseAsync(body, true, write.Token);
            object? effects = null;
            await ConfirmRegionDeletionCoreAsync(write.TripId, write.TargetId, write.Subject, choices, write.Token, write.Readmit,
                write.Web ? "web" : "bearer", async (snapshot, result) => effects = await CaptureRegionAsync(snapshot, result, write.Token));
            return new(true, effects ?? throw new InvalidOperationException("Missing Region deletion receipt effects."));
        }
        catch (RegionDeletionRejectedException error)
        {
            var code = error.Code switch
            {
                "not-found" => "mutation-scope-unavailable",
                "forbidden" => "mutation-operation-forbidden",
                _ => error.Code
            };
            throw new TripMetadataMutationRejected(error.Status, code, error.Message);
        }
    }

    /// <summary>Supplies an atomic projection of a confirmed Place deletion and exact related effects.</summary>
    internal async Task<TripStructureResult> DeletePlaceWithReceiptAsync(TripStructureWrite write)
    {
        using var body = TripStructureScalars.Body(write.Payload);
        var confirmation = await PlaceDeletionRequestParser.ParseAsync(body, true, write.Token);
        object? effects = null;
        await ConfirmPlaceDeletionCoreAsync(write.TripId, write.TargetId, write.Subject, confirmation, write.Token, write.Readmit,
            async (snapshot, result) =>
            {
                var deleted = result.DeletedIds.Segments;
                var removedPlanning = snapshot.Segments.Where(segment => deleted.Contains(segment.Id))
                    .Select(segment => Planning("segment", segment.Id, segment.Planning));
                effects = new
                {
                    snapshot.Place.Id, snapshot.Place.RegionId,
                    planningDigest = TripStructureDigests.Planning(snapshot.Place.Planning),
                    deletedDependentPlanning = SortedPlanning(removedPlanning),
                    placeOrder = await TripStructureCollections.PlaceOrderAsync(_dbContext, snapshot.Place.RegionId, write.Token),
                    segmentEffects = await TripStructureSegmentEffects.ReadAsync(_dbContext, snapshot.Trip.Id, snapshot.Segments,
                        snapshot.SegmentOrders, deleted, result.Data.UpdatedSegmentIds, write.Token),
                    visitEffects = new { unlinkedEvents = snapshot.Visits.Length, deletedCandidates = snapshot.Candidates.Length }
                };
            });
        return new(true, effects ?? throw new InvalidOperationException("Missing Place deletion receipt effects."));
    }

    /// <summary>Captures only private digest evidence and saved identities after the lifecycle's atomic writes.</summary>
    private async Task<object> CaptureRegionAsync(RegionDeletionSnapshot s,
        EditorMutationResult<RegionDeletionResultDto> result, CancellationToken token)
    {
        var removedPlanning = s.Places.Where(row => result.DeletedIds.Places.Contains(row.Id)).Select(row => Planning("place", row.Id, row.Planning))
            .Concat(s.Areas.Where(row => result.DeletedIds.Areas.Contains(row.Id)).Select(row => Planning("area", row.Id, row.Planning)))
            .Concat(s.Segments.Where(row => result.DeletedIds.Segments.Contains(row.Id)).Select(row => Planning("segment", row.Id, row.Planning)));
        var destination = s.Destination.RegionId;
        var places = result.Data.MovedPlaceIds.Count == 0 ? [] : s.Places.OrderBy(row => row.Id)
            .Select(row => new { row.Id, fromRegionId = s.Source.Id, toRegionId = destination, row.DisplayOrder }).ToArray();
        var areas = result.Data.MovedAreaIds.Count == 0 ? [] : s.Areas.OrderBy(row => row.Id)
            .Select(row => new { row.Id, fromRegionId = s.Source.Id, toRegionId = destination, row.DisplayOrder }).ToArray();
        return new
        {
            s.Source.Id, result.Data.Places, result.Data.Areas, destinationMode = s.Destination.Mode, destinationRegionId = destination,
            movedPlaces = places, movedAreas = areas,
            deletedPlaceIds = result.DeletedIds.Places.Order().ToArray(), deletedAreaIds = result.DeletedIds.Areas.Order().ToArray(),
            planningDigest = TripStructureDigests.Planning(s.Source.Planning), deletedDependentPlanning = SortedPlanning(removedPlanning),
            regionOrder = await TripStructureCollections.RegionOrderAsync(_dbContext, s.Trip.Id, token),
            placeOrders = places.Length == 0 ? [] : new[] { await TripStructureCollections.PlaceOrderAsync(_dbContext, destination!.Value, token) },
            areaOrders = areas.Length == 0 ? [] : new[] { await ReadAreaOrderAsync(destination!.Value, token) },
            segmentEffects = await TripStructureSegmentEffects.ReadAsync(_dbContext, s.Trip.Id, s.Segments, s.SegmentOrders,
                result.DeletedIds.Segments, result.Data.UpdatedSegmentIds, token),
            visitEffects = new { unlinkedEvents = s.Links.Events.Length, deletedCandidates = s.Links.Candidates.Length }
        };
    }

    /// <summary>Freezes linked events and candidates once locked Places exclude new foreign-key references.</summary>
    private async Task LockVisitLinksAsync(Guid[] placeIds, CancellationToken token)
    {
        if (placeIds.Length == 0) return;
        await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT "Id" FROM public."PlaceVisitEvents" WHERE "PlaceId" = ANY({placeIds}) ORDER BY "Id" FOR UPDATE
            """, token);
        await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT "Id" FROM public."PlaceVisitCandidates" WHERE "PlaceId" = ANY({placeIds}) ORDER BY "Id" FOR UPDATE
            """, token);
    }

    /// <summary>Reads the full current link set under the preview snapshot or the destructive PostgreSQL barriers.</summary>
    private async Task<TripDeletionVisitLinks> ReadVisitLinksAsync(Guid[] placeIds, CancellationToken token)
    {
        if (placeIds.Length == 0) return new([], []);
        var events = await _dbContext.PlaceVisitEvents.Where(row => row.PlaceId.HasValue && placeIds.Contains(row.PlaceId.Value))
            .OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync(token);
        var candidates = await _dbContext.PlaceVisitCandidates.Where(row => placeIds.Contains(row.PlaceId))
            .OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync(token);
        return new(events, candidates);
    }

    /// <summary>Uses database collation and exact stored Area orders for the committed destination.</summary>
    private async Task<object> ReadAreaOrderAsync(Guid regionId, CancellationToken token) => new
    {
        regionId,
        items = await _dbContext.Areas.AsNoTracking().Where(row => row.RegionId == regionId).OrderBy(row => row.DisplayOrder == null)
            .ThenBy(row => row.DisplayOrder).ThenBy(row => row.Name).ThenBy(row => row.Id)
            .Select(row => new { row.Id, row.DisplayOrder }).ToArrayAsync(token)
    };

    /// <summary>Omits rows without private Planning from dependent loss evidence.</summary>
    private static DeletedPlanning? Planning(string type, Guid id, PlanningAggregate? value) =>
        TripStructureDigests.Planning(value) is { } digest ? new(type, id, digest) : null;

    /// <summary>Sorts and projects only the actual removed rows carrying private Planning.</summary>
    private static DeletedPlanning[] SortedPlanning(IEnumerable<DeletedPlanning?> rows) =>
        rows.OfType<DeletedPlanning>().OrderBy(row => row.Type, StringComparer.Ordinal).ThenBy(row => row.Id).ToArray();

    private sealed record DeletedPlanning(string Type, Guid Id, string PlanningDigest);
}

/// <summary>Private identities used for consent and exact counts. Historical responses expose only the counts.</summary>
internal sealed record TripDeletionVisitLinks(Guid[] Events, Guid[] Candidates);
