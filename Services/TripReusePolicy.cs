using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Application reuse eligibility, separate from inherited rights and public online viewing.</summary>
public static class TripReusePolicy
{
    /// <summary>Only an explicit supported grant permits another user's copy/download.</summary>
    public static bool GrantsReuse(Trip trip) => trip.IsPublic && trip.ContributionPolicy is "CC-BY-4.0" or "CC0-1.0";

    /// <summary>Owners retain authorized export access; inactive/missing accounts never grant acquisition.</summary>
    public static bool Allows(Trip trip, string? caller, bool activeOwner, bool activeCaller) =>
        activeOwner && (caller == trip.UserId && activeCaller || GrantsReuse(trip));

    /// <summary>Reads current scalar authority independent of tracked preflight state, including at final delivery.</summary>
    public static async Task RequireAsync(ApplicationDbContext db, Guid tripId, string? caller, CancellationToken token = default)
    {
        var trip = await db.Trips.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tripId, token);
        var owner = trip != null && await db.Users.AsNoTracking().AnyAsync(u => u.Id == trip.UserId && u.IsActive, token);
        var active = caller != null && await db.Users.AsNoTracking().AnyAsync(u => u.Id == caller && u.IsActive, token);
        if (trip == null || !Allows(trip, caller, owner, active)) throw new UnauthorizedAccessException("Trip reuse is unavailable.");
    }

    /// <summary>Cookie identity is used only by cookie-protected export entry points, never bearer mutations.</summary>
    internal static string? Caller(IHttpContextAccessor? context) => context?.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
}
