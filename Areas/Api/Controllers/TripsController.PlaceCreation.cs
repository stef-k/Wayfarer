using System.Transactions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Configuration;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Areas.Api.Controllers;

public partial class TripsController
{
    /// <summary>Creates a Place and any missing exact fallback atomically, with one qualified collision recovery.</summary>
    [HttpPost("{tripId}/places")]
    public async Task<IActionResult> CreatePlace(Guid tripId, [FromBody] PlaceCreateRequestDto request)
    {
        var user = GetUserFromToken();
        if (user == null) return Unauthorized("Missing or invalid API token.");
        var cancellationToken = HttpContext.RequestAborted;
        var trip = await _dbContext.Trips
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == user.Id, cancellationToken);
        if (trip == null) return NotFound("Trip not found.");
        if (request == null) return BadRequest("Invalid request.");
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest("Name is required.");

        // Preserve explicit destination ownership precedence before coordinate validation.
        Region? destination = null;
        if (request.RegionId.HasValue)
        {
            destination = await _dbContext.Regions
                .FirstOrDefaultAsync(r => r.Id == request.RegionId.Value, cancellationToken);
            if (destination == null || destination.TripId != tripId || destination.UserId != user.Id)
                return BadRequest("Invalid regionId.");
        }

        Point? location = null;
        if (request.Latitude.HasValue || request.Longitude.HasValue)
        {
            if (!(request.Latitude.HasValue && request.Longitude.HasValue))
                return BadRequest("Both latitude and longitude must be provided together.");
            var latitude = request.Latitude.Value;
            var longitude = request.Longitude.Value;
            if (!IsValidCoordinate(latitude, longitude))
                return BadRequest("Latitude or Longitude is out of range.");
            location = new Point(longitude, latitude) { SRID = 4326 };
        }

        var createdFallback = false;
        if (destination == null)
        {
            var candidates = await ReadFallbacksAsync(_dbContext, tripId, cancellationToken);
            if (candidates.Length > 1 || candidates.Any(region => region.UserId != user.Id))
                return UnassignedRegionConflict();
            destination = candidates.SingleOrDefault();
            if (destination == null)
            {
                destination = new Region
                {
                    Id = Guid.NewGuid(), TripId = tripId, UserId = user.Id,
                    Name = ShadowRegionName, DisplayOrder = 0
                };
                _dbContext.Regions.Add(destination);
                createdFallback = true;
            }
        }

        // EF owns rollback/disposal for its implicit save transaction. Caller-owned transactions cannot recover here.
        var mayRecover = createdFallback && _dbContext.Database.IsNpgsql()
            && _dbContext.Database.CurrentTransaction == null
            && _dbContext.Database.GetEnlistedTransaction() == null
            && System.Transactions.Transaction.Current == null;
        try
        {
            return await InsertPlaceAsync(_dbContext, destination, user.Id, request, location, cancellationToken);
        }
        catch (DbUpdateException failure) when (mayRecover && !cancellationToken.IsCancellationRequested
            && failure.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: RegionConfiguration.UnassignedIndex
            })
        {
            // SaveChanges has unwound its implicit transaction; never attach this failed graph to another context.
            _dbContext.ChangeTracker.Clear();
            return await RecoverPlaceAsync(tripId, user.Id, request, location, cancellationToken);
        }
    }

    /// <summary>Revalidates bearer authority and one owned winner in a fresh DI-owned scope; never creates a fallback.</summary>
    private async Task<IActionResult> RecoverPlaceAsync(Guid tripId, string originalUserId,
        PlaceCreateRequestDto request, Point? location, CancellationToken cancellationToken)
    {
        await using var scope = HttpContext.RequestServices.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await new IncomingApiTokenResolver(db, ApiWorkAdmission.TokenLookups)
            .ResolveAsync(HttpContext, cancellationToken);
        if (user == null || user.Id != originalUserId) return Unauthorized("Missing or invalid API token.");
        var trip = await db.Trips.SingleOrDefaultAsync(t => t.Id == tripId && t.UserId == user.Id, cancellationToken);
        if (trip == null) return NotFound("Trip not found.");
        var candidates = await ReadFallbacksAsync(db, tripId, cancellationToken);
        if (candidates.Length != 1 || candidates[0].UserId != user.Id) return UnassignedRegionConflict();
        // This second save is deliberately outside the collision catch: no further retry, including a new collision.
        return await InsertPlaceAsync(db, candidates[0], user.Id, request, location, cancellationToken);
    }

    /// <summary>Reads candidates without hiding malformed ownership or electing an arbitrary winner.</summary>
    private static Task<Region[]> ReadFallbacksAsync(ApplicationDbContext db, Guid tripId, CancellationToken token) =>
        db.Regions.Where(region => region.TripId == tripId && region.Name == ShadowRegionName)
            .Take(2).ToArrayAsync(token);

    /// <summary>Saves only the new Place, its staged parent if any, and the normal Trip timestamp.</summary>
    private async Task<IActionResult> InsertPlaceAsync(ApplicationDbContext db, Region destination, string userId,
        PlaceCreateRequestDto request, Point? location, CancellationToken cancellationToken)
    {
        var order = request.DisplayOrder ?? (await db.Places.Where(place => place.RegionId == destination.Id)
            .MaxAsync(place => (int?)place.DisplayOrder, cancellationToken) ?? 0) + 1;
        var place = new Place
        {
            Id = Guid.NewGuid(), UserId = userId, RegionId = destination.Id, Region = destination,
            Name = request.Name!, Notes = RichNotes.Normalize(request.Notes), Location = location,
            DisplayOrder = order,
            IconName = string.IsNullOrWhiteSpace(request.IconName) ? "marker" : request.IconName!,
            MarkerColor = string.IsNullOrWhiteSpace(request.MarkerColor) ? "bg-blue" : request.MarkerColor!
        };
        db.Places.Add(place);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true, place = place.ToApiDto() });
    }

    /// <summary>Returns the approved owner-scoped reconciliation response without exposing conflicting rows.</summary>
    private ConflictObjectResult UnassignedRegionConflict() => Conflict(new
    {
        error = "unassigned_region_conflict", message = "Unassigned Places requires reconciliation."
    });
}
