using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Areas.Api.Controllers;

public partial class TripsController
{
    /// <summary>Creates one Region against the incoming client's complete Region/canonical order originals.</summary>
    [HttpPost("{tripId:guid}/conditional/regions")]
    public async Task<IActionResult> CreateConditionalRegion(Guid tripId, CancellationToken cancellationToken)
    {
        var authority = IncomingApiTokenResolver.ForRequest(HttpContext, _dbContext);
        var user = await authority.ResolveAsync(HttpContext, cancellationToken);
        if (user == null) return Unauthorized();
        if (!await _dbContext.Trips.AsNoTracking().AnyAsync(t => t.Id == tripId && t.UserId == user.Id, cancellationToken)) return NotFound();
        try
        {
            var request = await CreationConditionalRequest.ReadAsync(Request.Body, false, cancellationToken);
            CreationConditionalRequest.ValidateValue(request.Value, false, false);
            var value = CreationConditionalRequest.Fields(request.Value, ["name", "notes"]);
            var name = CreationText(value, "name", false);
            if (string.Equals(name.Trim(), ShadowRegionName, StringComparison.OrdinalIgnoreCase)) throw CreationConditionalRequest.Invalid("Region name is reserved.");
            var region = new Region { Name = name.Trim(), Notes = CreationNotes(value) };
            var result = await new CheckedCreationService(_dbContext).RegionAsync(tripId, user.Id, request, region, (created, order) =>
                Task.FromResult(new { contract = "bd2-1", success = true, applied = true, tripId,
                    region = new ApiTripRegionDto { Id = created.Id, Name = created.Name, Notes = created.Notes, DisplayOrder = created.DisplayOrder },
                    regionOrder = order }), token => authority.RevalidateMutationAsync(HttpContext, user.Id, token), cancellationToken);
            await _warmupScheduler.ScheduleWarmupAsync(tripId);
            return Ok(result);
        }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }

    /// <summary>Creates an unlocated or coordinate-supplied Place without provider acquisition or Location-history writes.</summary>
    [HttpPost("{tripId:guid}/conditional/places")]
    public async Task<IActionResult> CreateConditionalPlace(Guid tripId, CancellationToken cancellationToken)
    {
        var authority = IncomingApiTokenResolver.ForRequest(HttpContext, _dbContext);
        var user = await authority.ResolveAsync(HttpContext, cancellationToken);
        if (user == null) return Unauthorized();
        if (!await _dbContext.Trips.AsNoTracking().AnyAsync(t => t.Id == tripId && t.UserId == user.Id, cancellationToken)) return NotFound();
        try
        {
            var request = await CreationConditionalRequest.ReadAsync(Request.Body, true, cancellationToken);
            CreationConditionalRequest.ValidateValue(request.Value, true, false);
            var value = CreationConditionalRequest.Fields(request.Value, ["regionId", "name", "notes", "location", "iconName", "markerColor"]);
            var regionValue = CreationConditionalRequest.Required(value, "regionId");
            var regionId = regionValue.ValueKind == JsonValueKind.Null ? (Guid?)null : CreationConditionalRequest.Id(regionValue);
            if (regionId != request.RegionId) throw CreationConditionalRequest.Invalid("The desired destination must match the original destination.");
            if (regionId.HasValue && !await _dbContext.Regions.AsNoTracking().AnyAsync(r => r.Id == regionId && r.TripId == tripId && r.UserId == user.Id, cancellationToken)) return NotFound();
            var location = CreationConditionalRequest.Required(value, "location");
            var place = new Place { Name = CreationText(value, "name", false).Trim(), Notes = CreationNotes(value),
                IconName = CreationText(value, "iconName", true), MarkerColor = CreationText(value, "markerColor", true), Location = CreationPoint(location) };
            if (string.IsNullOrWhiteSpace(place.IconName)) place.IconName = "marker";
            if (string.IsNullOrWhiteSpace(place.MarkerColor)) place.MarkerColor = "bg-blue";
            var result = await new CheckedCreationService(_dbContext).PlaceAsync(tripId, user.Id, request, place, async created =>
            {
                var order = await _dbContext.Places.AsNoTracking().Where(p => p.RegionId == created.RegionId)
                    .OrderBy(p => p.DisplayOrder == null).ThenBy(p => p.DisplayOrder).ThenBy(p => p.Id)
                    .Select(p => new { p.Id, p.DisplayOrder }).ToArrayAsync(cancellationToken);
                return new { contract = "bd2-1", success = true, applied = true, tripId, place = created.ToApiDto(), placeOrder = order,
                    createdFallback = request.RegionId == null ? new { id = created.RegionId, name = ShadowRegionName, displayOrder = 0 } : null };
            }, token => authority.RevalidateMutationAsync(HttpContext, user.Id, token), cancellationToken);
            return Ok(result);
        }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }

    /// <summary>Requires present strings; only marker fields may use empty reset values.</summary>
    private static string CreationText(Dictionary<string, JsonElement> value, string key, bool empty)
    {
        var item = CreationConditionalRequest.Required(value, key);
        if (item.ValueKind != JsonValueKind.String || !empty && string.IsNullOrWhiteSpace(item.GetString()))
            throw CreationConditionalRequest.Invalid($"{key} must be a valid string.");
        return item.GetString()!;
    }

    /// <summary>Reuses canonical Notes persistence and rejects embedded image payloads.</summary>
    private static string? CreationNotes(Dictionary<string, JsonElement> value)
    {
        var item = CreationConditionalRequest.Required(value, "notes");
        if (item.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw CreationConditionalRequest.Invalid("notes must be a string or null.");
        var notes = item.GetString();
        if (RichNotes.ContainsDataImageSource(notes)) throw CreationConditionalRequest.Invalid("Notes images must use external URLs.");
        return RichNotes.Normalize(notes);
    }

    /// <summary>Accepts null or one finite WGS84 pair, including zeroes.</summary>
    private static Point? CreationPoint(JsonElement location)
    {
        if (location.ValueKind == JsonValueKind.Null) return null;
        var fields = CreationConditionalRequest.Fields(location, ["latitude", "longitude"]);
        var latitude = CreationConditionalRequest.Required(fields, "latitude");
        var longitude = CreationConditionalRequest.Required(fields, "longitude");
        if (latitude.ValueKind != JsonValueKind.Number || longitude.ValueKind != JsonValueKind.Number
            || !latitude.TryGetDouble(out var lat) || !longitude.TryGetDouble(out var lon) || !IsValidCoordinate(lat, lon))
            throw CreationConditionalRequest.Invalid("location requires a finite WGS84 coordinate pair.");
        return new Point(lon, lat) { SRID = 4326 };
    }
}
