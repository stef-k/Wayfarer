using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>Owner-only portable-key discovery and terminal ingestion decisions.</summary>
public partial class LocationController
{
    /// <summary>Classifies only the caller's bounded known-key list in one read-only statement snapshot.</summary>
    [HttpPost("key-status")]
    public async Task<IActionResult> KeyStatus([FromBody] KeyStatusRequest? request)
    {
        Response.Headers.CacheControl = "no-store";
        var user = GetUserFromToken();
        if (user == null) return Unauthorized("Invalid or missing API token.");
        if (request?.Keys is not { Length: >= 1 and <= 100 })
            return BadRequest("Supply between 1 and 100 UUID keys.");
        var keys = new List<Guid>();
        foreach (var value in request.Keys)
        {
            if (!Guid.TryParse(value, out var key)) return BadRequest("Invalid UUID key.");
            if (!keys.Contains(key)) keys.Add(key);
        }
        var requested = keys.ToArray();
        // UNION ALL keeps markers and winners in the same MVCC snapshot; no history enumeration or locking.
        var known = await _dbContext.Set<LocationDeletionMarker>().AsNoTracking()
            .Where(row => row.UserId == user.Id && requested.Contains(row.IdempotencyKey))
            .Select(row => new { Key = row.IdempotencyKey, LocationId = (int?)null, LocalTimestamp = (DateTime?)null })
            .Concat(_dbContext.Locations.AsNoTracking()
                .Where(row => row.UserId == user.Id && row.IdempotencyKey.HasValue && requested.Contains(row.IdempotencyKey.Value))
                .Select(row => new { Key = row.IdempotencyKey!.Value, LocationId = (int?)row.Id,
                    LocalTimestamp = (DateTime?)row.LocalTimestamp }))
            .ToListAsync(HttpContext.RequestAborted);
        var results = keys.Select(key =>
        {
            var rows = known.Where(row => row.Key == key).ToList();
            if (rows.Any(row => row.LocationId == null)) return (object)new { key, state = "deleted" };
            var present = rows.FirstOrDefault();
            return present == null ? (object)new { key, state = "unknown" }
                : new { key, state = "present", locationId = present.LocationId,
                    localTimestamp = present.LocalTimestamp };
        });
        return Ok(new { success = true, results });
    }

    /// <summary>A confirmed deletion never carries editable linkage, skipped success or retry advice.</summary>
    private ObjectResult DeletedLocationResult()
    {
        Response.Headers.Remove("Retry-After");
        return StatusCode(410, new { success = false, code = "location_deleted", message = "Location was deleted." });
    }

    /// <summary>Serializes a terminal passive skip and its optional Visit with deletion and keyed winner insertion.</summary>
    private async Task<IActionResult> SkipOrReplayAsync(string userId, Guid? key, GpsLoggerLocationDto dto,
        Point? coordinates = null)
    {
        await using var transaction = key.HasValue
            ? await LocationDeletionFence.BeginAsync(_dbContext, userId, HttpContext.RequestAborted) : null;
        if (await LocationDeletionFence.IsDeletedAsync(_dbContext, userId, key, HttpContext.RequestAborted))
            return DeletedLocationResult();
        var locationId = key.HasValue
            ? await _dbContext.Locations.AsNoTracking().Where(row => row.UserId == userId && row.IdempotencyKey == key.Value)
                .Select(row => (int?)row.Id).FirstOrDefaultAsync(HttpContext.RequestAborted) : null;
        if (key.HasValue && locationId == null && coordinates != null)
            await ProcessAutomaticVisitAsync(userId, coordinates, dto.Accuracy, transaction);
        if (transaction != null) await transaction.CommitAsync(HttpContext.RequestAborted);
        return Ok(new { success = true, skipped = !locationId.HasValue, locationId });
    }

    /// <summary>Only keys are accepted; callers cannot supply a target account or Group.</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed class KeyStatusRequest
    {
        /// <summary>One to one hundred parseable UUID spellings; aliases collapse to one result.</summary>
        public string?[]? Keys { get; set; }
    }
}
