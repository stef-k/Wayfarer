using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

public sealed partial class PlaceRegionLifecycleService
{
    /// <summary>Compares retained public originals and prepares a coherent, explicitly reviewed private dependency base.</summary>
    internal async Task<object> PreparePlaceStructureAsync(Guid tripId, Guid targetId, string userId, PlaceStructuralRequest request,
        IReadOnlySet<string> icons, IReadOnlySet<string> colors, CancellationToken token)
    {
        await using var transaction = await BeginTransactionAsync(token, IsolationLevel.RepeatableRead);
        var snapshot = await ReadPlaceStructureAsync(tripId, targetId, userId, request, token);
        ValidatePlaceStructure(snapshot, request, icons, colors);
        var fingerprint = PlaceStructureFingerprint(snapshot, request);
        return new { contract = "bd2-1", operation = request.Operation, tripId, targetId, requiresReview = true,
            place = snapshot.Place == null ? null : PlaceStructuralRequest.Original(snapshot.Place),
            placesByRegion = snapshot.Siblings.GroupBy(p => p.RegionId).ToDictionary(g => g.Key, g => g.OrderBy(p => p.Id).Select(PlaceStructuralRequest.Original).ToArray()),
            affectedSegmentIds = snapshot.Segments.Select(s => s.Id).ToArray(),
            conditionToken = _confirmation.IssuePlaceStructure(request.Operation, userId, tripId, targetId, request.IntentFingerprint(), fingerprint) };
    }

    /// <summary>One compare/apply transaction for coordinate, move, vector, and combined Web intent; never retries unknown outcomes.</summary>
    internal async Task<EditorMutationResult<object>> ApplyPlaceStructureAsync(Guid tripId, Guid targetId, string userId,
        PlaceStructuralRequest request, IReadOnlySet<string> icons, IReadOnlySet<string> colors,
        Func<CancellationToken, Task<bool>>? revalidateBearer,
        Func<Place, Task<(string Address, EditorPlaceMetadataDirective Metadata)>>? explicitAddress,
        CancellationToken token)
    {
        var candidates = await ReadPlaceStructureAsync(tripId, targetId, userId, request, token);
        ValidatePlaceStructure(candidates, request, icons, colors);
        if (!request.Reviewed || string.IsNullOrWhiteSpace(request.ConditionToken))
            throw new TripMetadataMutationRejected(409, "mutation-review-required", "Keep your draft. Review the prepared dependency base before applying this operation.");
        var address = explicitAddress == null ? ((string Address, EditorPlaceMetadataDirective Metadata)?)null
            : await explicitAddress(candidates.Place!);
        var tracker = LifecycleTrackerSnapshot.Capture(_dbContext);
        var scope = new LifecycleRecoveryScope(candidates.Locks.PlaceIds, candidates.Locks.RegionIds, candidates.Locks.SegmentIds);
        var isolated = false;
        var commitStarted = false;
        await using var transaction = await BeginTransactionAsync(token, IsolationLevel.ReadCommitted);
        try
        {
            await LockAsync(candidates.Locks, token);
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Trips\" WHERE \"Id\" = {tripId} FOR UPDATE", token);
            var active = await _dbContext.Users.FromSqlInterpolated($"SELECT * FROM public.\"AspNetUsers\" WHERE \"Id\" = {userId} AND \"IsActive\" FOR SHARE")
                .AsNoTracking().AnyAsync(token);
            if (!active || revalidateBearer != null && !await revalidateBearer(token))
                throw new TripMetadataMutationRejected(401, "mutation-authority-unavailable", "Authentication is no longer valid.");
            var current = await ReadPlaceStructureAsync(tripId, targetId, userId, request, token);
            ValidatePlaceStructure(current, request, icons, colors);
            if (current.Locks.RequiresLocksOutside(candidates.Locks)
                || !_confirmation.MatchesPlaceStructure(request.ConditionToken, request.Operation, userId, tripId, targetId,
                    request.IntentFingerprint(), PlaceStructureFingerprint(current, request))) throw PlaceStructureStale();

            foreach (var entry in _dbContext.ChangeTracker.Entries().ToArray()) entry.State = EntityState.Detached;
            isolated = true;
            var result = await ApplyLockedPlaceStructureAsync(current, request, address, token);
            commitStarted = true;
            if (transaction != null) await transaction.CommitAsync(token);
            return result;
        }
        catch (Exception original)
        {
            await RecoverUnappliedRegionDeletionAsync(original, transaction);
            if (!commitStarted && original is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected })
                throw new TripMetadataMutationRejected(409, "mutation-contention", "Contention prevented this operation. Keep your draft and review current state.");
            throw;
        }
        finally
        {
            if (isolated) RestoreAfterRegionDeletion(scope, tracker);
        }
    }

    /// <summary>Reuses scalar publication validation and preserves exact public originals through both prepare and final locks.</summary>
    private static void ValidatePlaceStructure(PlaceStructuralSnapshot snapshot, PlaceStructuralRequest request,
        IReadOnlySet<string> icons, IReadOnlySet<string> colors)
    {
        var expected = request.Expected;
        var place = snapshot.Place;
        if (request.Operation == "order")
        {
            if (!expected.TryGetProperty("places", out var originals) || !OriginalPlacesMatch(originals, snapshot.Siblings)) throw PlaceStructureStale();
            if (!request.Orders[Guid.Empty].Order().SequenceEqual(snapshot.Siblings.Select(p => p.Id).Order())) throw PlaceStructuralRequest.Invalid();
            return;
        }
        if (request.Destination.HasValue)
        {
            if (request.Destination == place!.RegionId || !expected.TryGetProperty("place", out var original)
                || !PlaceStructuralRequest.Equal(original, PlaceStructuralRequest.Original(place))) throw PlaceStructureStale();
            if (!expected.TryGetProperty("placesByRegion", out var regions) || !OriginalOrdersMatch(regions, snapshot)) throw PlaceStructureStale();
            ValidateMoveVectors(snapshot, request);
        }
        else
        {
            var fields = PlaceStructuralRequest.Fields(expected, "regionId", "location", "name", "notesHtml", "iconName", "markerColor", "address");
            if (!fields.TryGetValue("regionId", out var region) || !PlaceStructuralRequest.Equal(region, place!.RegionId)) throw PlaceStructureStale();
            if (snapshot.Coordinates && (!fields.TryGetValue("location", out var location) || !PlaceStructuralRequest.Equal(location, PlaceStructuralRequest.Pair(place.Location)))) throw PlaceStructureStale();
        }
        if (request.Operation != "web-update") return;
        foreach (var field in request.Changes.EnumerateObject())
        {
            if (field.Name is "regionId" or "location" or "reverseGeocode") continue;
            var originalKey = field.Name == "notesHtml" && request.Destination.HasValue ? "notes" : field.Name;
            var originals = request.Destination.HasValue ? expected.GetProperty("place") : expected;
            if (!originals.TryGetProperty(originalKey, out var original)) throw PlaceStructuralRequest.Invalid();
            if (field.Name == "address")
            {
                if (!PlaceStructuralRequest.Equal(original, place!.Address)) throw PlaceStructureStale();
                continue;
            }
            var key = field.Name == "notesHtml" ? "notes" : field.Name;
            PlaceConditionalRequest.Value(field.Name, field.Value, true, icons, colors);
            var value = PlaceConditionalRequest.Value(field.Name, original, false, icons, colors);
            if (!Equals(value, PlaceConditionalMutationService.Current(place!, key))) throw PlaceStructureStale();
        }
        if (request.Changes.TryGetProperty("reverseGeocode", out var reverse) && reverse.GetBoolean())
        {
            var originals = request.Destination.HasValue ? expected.GetProperty("place") : expected;
            if (!originals.TryGetProperty("address", out var address) || !PlaceStructuralRequest.Equal(address, place!.Address)
                || !originals.TryGetProperty("location", out var location) || !PlaceStructuralRequest.Equal(location, PlaceStructuralRequest.Pair(place.Location))) throw PlaceStructureStale();
        }
    }

    private static bool OriginalPlacesMatch(JsonElement originals, Place[] places) => originals.ValueKind == JsonValueKind.Array
        && PlaceStructuralRequest.Equal(JsonSerializer.SerializeToElement(originals.EnumerateArray().OrderBy(p => p.GetProperty("id").GetGuid()), PlaceStructuralRequest.Json),
            places.OrderBy(p => p.Id).Select(PlaceStructuralRequest.Original).ToArray());

    private static bool OriginalOrdersMatch(JsonElement originals, PlaceStructuralSnapshot snapshot)
    {
        if (originals.ValueKind != JsonValueKind.Object) return false;
        var fields = originals.EnumerateObject().ToArray();
        return fields.Length == snapshot.Regions.Length && fields.Select(f => f.Name).Distinct().Count() == fields.Length
            && snapshot.Regions.All(r => originals.TryGetProperty(r.Id.ToString(), out var values)
                && values.ValueKind == JsonValueKind.Array && PlaceStructuralRequest.Equal(
                    JsonSerializer.SerializeToElement(values.EnumerateArray().OrderBy(p => p.GetProperty("id").GetGuid())),
                    snapshot.Siblings.Where(p => p.RegionId == r.Id).OrderBy(p => p.Id).Select(p => new { p.Id, p.DisplayOrder }).ToArray()));
    }

    /// <summary>A move transfers only its selected Place; every other membership and each complete vector stays exact.</summary>
    private static void ValidateMoveVectors(PlaceStructuralSnapshot snapshot, PlaceStructuralRequest request)
    {
        var source = snapshot.Place!.RegionId;
        var destination = request.Destination!.Value;
        if (request.Orders.Count != 2 || !request.Orders.ContainsKey(source) || !request.Orders.ContainsKey(destination)) throw PlaceStructuralRequest.Invalid();
        foreach (var (regionId, ids) in request.Orders)
        {
            var required = snapshot.Siblings.Where(p => p.RegionId == regionId && p.Id != snapshot.Place.Id).Select(p => p.Id)
                .Concat(regionId == destination ? new[] { snapshot.Place.Id } : []).Order();
            if (!ids.Order().SequenceEqual(required)) throw PlaceStructuralRequest.Invalid();
        }
    }

    /// <summary>Material conflicts prove no write only before commit starts; callers retain drafts instead of rebasing.</summary>
    private static TripMetadataMutationRejected PlaceStructureStale() => new(409, "mutation-dependency-stale", "The original Place, order or dependencies changed. Keep your draft and explicitly review a new base.");
}
