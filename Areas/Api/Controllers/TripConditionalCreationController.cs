using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;
using static Wayfarer.Models.Dtos.CreationConditionalRequest;

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
}
