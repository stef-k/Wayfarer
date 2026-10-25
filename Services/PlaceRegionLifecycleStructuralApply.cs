using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Util;

namespace Wayfarer.Services;

public sealed partial class PlaceRegionLifecycleService
{
    /// <summary>Applies prepared intent with existing scalar/enrichment and locked Segment reconciliation owners.</summary>
    private async Task<EditorMutationResult<object>> ApplyLockedPlaceStructureAsync(PlaceStructuralSnapshot snapshot,
        PlaceStructuralRequest request, PlaceStructuralAddress? address, CancellationToken token)
    {
        var tripId = snapshot.Trip.Id;
        var userId = snapshot.Trip.UserId;
        var changedSegments = Array.Empty<Guid>();
        var locationChanged = false;
        Place? place = null;
        if (snapshot.Place != null)
        {
            place = await LoadOwnedPlaceAsync(tripId, snapshot.Place.Id, userId, token) ?? throw PlaceStructureUnavailable();
            var update = new PlaceLifecycleUpdate(place.RegionId, place.Name, place.Notes, place.Address, place.IconName,
                place.MarkerColor, place.Location, EditorMetadata: new(EditorPlaceMetadataPolicy.RetainCompatible));
            if (request.Operation == "coordinates") update = update with { Location = request.Location };
            if (request.Operation == "web-update")
            {
                foreach (var field in request.Changes.EnumerateObject())
                    update = field.Name switch
                    {
                        "name" => update with { Name = field.Value.GetString()!.Trim() },
                        "notesHtml" when field.Value.ValueKind != System.Text.Json.JsonValueKind.Null => update with { Notes = RichNotes.Normalize(field.Value.GetString()) },
                        "iconName" => update with { IconName = field.Value.GetString() },
                        "markerColor" => update with { MarkerColor = field.Value.GetString() },
                        "address" => update with { Address = field.Value.GetString()?.Trim() ?? string.Empty },
                        "location" => update with { Location = request.Location },
                        _ => update
                    };
            }
            if (address != null) update = update with { Address = address.Address, EditorMetadata = address.Metadata };
            locationChanged = !CoordinatesEqual(place.Location, update.Location);
            var affected = locationChanged ? await LoadAffectedSegmentsAsync(tripId, userId, [place.Id], token) : [];
            if (update.Location == null && affected.Any(s => s.Waypoints.Count > 0)) throw PlaceStructuralRequest.Invalid();
            ApplyScalarUpdate(place, update);
            if (request.Destination.HasValue)
            {
                var region = await _dbContext.Regions.SingleAsync(r => r.Id == request.Destination, token);
                place.RegionId = region.Id;
                place.Region = region;
            }
            foreach (var segment in affected)
            {
                RewriteLocation(segment, place.Id, update.Location);
                await ReconcileSegmentAsync(segment, token);
            }
            changedSegments = affected.Select(s => s.Id).ToArray();
        }
        var orders = request.Operation == "order" ? new Dictionary<Guid, Guid[]> { [snapshot.Regions.Single().Id] = request.Orders[Guid.Empty] } : request.Orders;
        if (orders.Count > 0)
        {
            var ids = orders.Values.SelectMany(ids => ids).ToArray();
            var places = await _dbContext.Places.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, token);
            foreach (var (_, vector) in orders)
                for (var index = 0; index < vector.Length; index++) places[vector[index]].DisplayOrder = index + 1;
        }
        // Explicit overload saves only the isolated operation tracker; current-safe freshness is in this same transaction.
        await _dbContext.SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken: token);
        var now = DateTime.UtcNow;
        if (_dbContext.Database.IsRelational())
            await _dbContext.Trips.Where(t => t.Id == tripId && t.UserId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.UpdatedAt, t => t.UpdatedAt > now ? t.UpdatedAt : now), token);
        else
        {
            var trip = await _dbContext.Trips.SingleAsync(t => t.Id == tripId, token);
            trip.UpdatedAt = trip.UpdatedAt > now ? trip.UpdatedAt : now;
            await _dbContext.SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken: token);
        }
        var reader = new TripEditorPlaceMutationReader(_dbContext, _confirmation.RegionDeletionAggregateTokens);
        var changedIds = orders.Count > 0 ? orders.Values.SelectMany(v => v).Distinct().ToArray() : new[] { place!.Id };
        var dtos = await reader.LoadPlaceDtosAsync(changedIds, tripId, userId, token);
        var segments = await reader.LoadSegmentDtosAsync(changedSegments, tripId, userId, token);
        var affectedSlices = await reader.BuildAffectedAsync(tripId, userId, dtos, orders.Keys.ToArray(), segments,
            locationChanged ? await reader.LoadSegmentOrderAsync(tripId, userId, token) : null, locationChanged, token);
        object data = request.Operation == "order" ? new EditorPlaceOrderResult(snapshot.Regions.Single().Id, orders.Values.Single())
            : dtos.Single(p => p.Id == place!.Id);
        return new(true, data, affectedSlices, EditorDeletedIdsDto.Empty, address?.Warnings ?? []) { Contract = "bd2-1" };
    }
}

/// <summary>Frozen explicit Web enrichment result, including the existing provider fallback warning.</summary>
internal sealed record PlaceStructuralAddress(string Address, EditorPlaceMetadataDirective Metadata, IReadOnlyList<EditorWarningDto> Warnings);
