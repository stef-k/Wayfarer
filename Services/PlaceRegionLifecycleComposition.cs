using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

public sealed partial class PlaceRegionLifecycleService
{
    /// <summary>Joins a composition's already held domain barriers, preserving the ordinary coordinate-review proof and core.</summary>
    internal async Task CompositionCoordinatesAsync(Guid tripId, Place place, NetTopologySuite.Geometries.Point? location,
        string? conditionToken, CancellationToken token)
    {
        RequireCompositionTransaction();
        var request = new PlaceStructuralRequest("coordinates", false,
            JsonSerializer.SerializeToElement(new { regionId = place.RegionId, location = PlaceStructuralRequest.Pair(place.Location) }, PlaceStructuralRequest.Json),
            default, null, [], location, conditionToken, true);
        var current = await ReadPlaceStructureAsync(tripId, place.Id, place.UserId, request, token);
        ValidatePlaceStructure(current, request, new HashSet<string>(), new HashSet<string>());
        if (!_confirmation.MatchesPlaceStructure(conditionToken, request.Operation, place.UserId, tripId, place.Id,
                request.IntentFingerprint(), PlaceStructureFingerprint(current, request))) throw PlaceStructureStale();
        await ApplyLockedPlaceStructureAsync(current, request, null, token);
    }

    /// <summary>Uses the existing deletion proof and dependency reconciliation inside the caller's workspace transaction.</summary>
    internal async Task CompositionRemoveAsync(Guid tripId, string owner, TripCompositionItem item, CancellationToken token)
    {
        RequireCompositionTransaction();
        if (item.Kind == "place")
        {
            var approval = _confirmation.ReadPlaceDeletion(item.ConditionToken ?? "");
            var current = await ReadPlaceDeletionAsync(tripId, item.DestinationId!.Value, owner, token);
            if (approval.UserId != owner || approval.TripId != tripId || approval.PlaceId != item.DestinationId
                || _confirmation.IsPlaceDeletionExpired(approval) || PlaceDeletionFingerprint(current) != approval.Fingerprint) throw StalePlaceDeletion();
            await ApplyPlaceDeletionAsync(current, owner, token);
            return;
        }
        if (item.Kind != "region" || item.Places is not ("keep" or "delete") || item.Areas is not ("keep" or "delete"))
            throw new TripMetadataMutationRejected(400, "composition-removal-invalid", "Select explicit child removal dispositions.");
        var request = new RegionDeletionRequestDto(item.Places, item.Areas, item.ConditionToken);
        var consent = _confirmation.ReadRegionDeletion(item.ConditionToken ?? "");
        var snapshot = await ReadRegionDeletionAsync(tripId, item.DestinationId!.Value, owner, request,
            consent.Destination.RegionId ?? Guid.NewGuid(), token);
        if (consent.UserId != owner || consent.TripId != tripId || consent.SourceId != item.DestinationId
            || consent.Places != item.Places || consent.Areas != item.Areas || _confirmation.IsRegionDeletionExpired(consent)
            || consent.Destination != snapshot.Destination || consent.Fingerprint != RegionDeletionFingerprint(snapshot))
            throw new TripMetadataMutationRejected(409, "composition-removal-stale", "Removal consequences changed. Review the current deletion preview.");
        // Region apply attaches its own canonical graph. The composition coordinator flushes and detaches before entering.
        await ApplyRegionDeletionAsync(snapshot, owner, request, token);
    }

    private void RequireCompositionTransaction()
    {
        if (_dbContext.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Composition lifecycle staging requires the outer transaction and held domain barriers.");
    }
}
