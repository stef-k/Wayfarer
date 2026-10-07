using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Util;

/// <summary>Seeds one run-owned User and public Trip for the existing shared-layout assertions.</summary>
public static class SharedLayoutFixture
{
    /// <summary>Dispatches managed provision and two separate exact-identity cleanup probes.</summary>
    public static async Task<bool> TryRunAsync(string command, string path, ApplicationDbContext db)
    {
        if (command == "provision-layout")
        {
            await ProvisionAsync(db, path);
            return true;
        }
        if (command is not ("cleanup-layout" or "verify-cleanup-layout")) return false;
        var manifest = JsonSerializer.Deserialize<LayoutManifest>(await File.ReadAllTextAsync(path), FixtureJson.Options)!;
        if (command == "cleanup-layout")
        {
            await db.Trips.Where(trip => trip.Id == manifest.TripId && trip.UserId == manifest.UserId).ExecuteDeleteAsync();
            await db.Users.Where(user => user.Id == manifest.UserId).ExecuteDeleteAsync();
        }
        if (await db.Users.AnyAsync(user => user.Id == manifest.UserId)
            || await db.Trips.AnyAsync(trip => trip.Id == manifest.TripId)
            || await db.ApiTokens.AnyAsync(token => token.UserId == manifest.UserId)
            || await db.UserRoles.AnyAsync(link => link.UserId == manifest.UserId)
            || await db.Regions.AnyAsync(region => region.Id == manifest.RegionId)
            || await db.Places.AnyAsync(place => place.Id == manifest.PlaceId))
            throw new InvalidOperationException("Shared-layout cleanup left captured fixture rows.");
        Console.WriteLine("Shared-layout fixture cleanup independently verified: User, Trip, token, role, Region, Place=0.");
        return true;
    }

    /// <summary>Records cleanup identities before insertion, including an initial connection token for replacement.</summary>
    private static async Task ProvisionAsync(ApplicationDbContext db, string path)
    {
        var run = Environment.GetEnvironmentVariable("WAYFARER_E2E_RUN_ID")!;
        var password = Environment.GetEnvironmentVariable("WAYFARER_E2E_PASSWORD")!;
        var user = new ApplicationUser
        {
            Id = $"browser-layout-{run}", UserName = $"browser-layout-{run}",
            NormalizedUserName = $"BROWSER-LAYOUT-{run}".ToUpperInvariant(),
            DisplayName = "Shared layout browser User", IsActive = true, EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("N"), ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };
        user.PasswordHash = new PasswordHasher<ApplicationUser>().HashPassword(user, password);
        var trip = new Trip
        {
            Id = Guid.NewGuid(), UserId = user.Id, User = user, Name = "Shared layout browser Trip",
            IsPublic = true, UpdatedAt = DateTime.UtcNow, CenterLat = 37.98, CenterLon = 23.73, Zoom = 12
        };
        var region = new Region { Id = Guid.NewGuid(), Trip = trip, UserId = user.Id, Name = "Browser region", DisplayOrder = 1 };
        var place = new Place
        {
            Id = Guid.NewGuid(), Region = region, UserId = user.Id, Name = "Browser place", DisplayOrder = 1,
            Location = new Point(23.73, 37.98) { SRID = 4326 }
        };
        region.Places.Add(place);
        trip.Regions.Add(region);
        var role = await db.Roles.SingleAsync(role => role.NormalizedName == "USER");
        db.Users.Add(user);
        db.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = role.Id });
        db.Trips.Add(trip);
        // Seed only a hash; the browser's existing replacement branch never touches a human token.
        db.ApiTokens.Add(new ApiToken
        {
            Name = ApiTokenService.ConnectionTokenName, User = user, UserId = user.Id, CreatedAt = DateTime.UtcNow,
            TokenHash = Convert.ToHexString(SHA256.HashData(RandomNumberGenerator.GetBytes(32))).ToLowerInvariant()
        });
        var manifest = new LayoutManifest(user.Id, user.UserName!, trip.Id, region.Id, place.Id);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, FixtureJson.Options));
        await db.SaveChangesAsync();
    }

    /// <summary>Contains identifiers only; the ephemeral password is delivered through child environment.</summary>
    private sealed record LayoutManifest(string UserId, string Username, Guid TripId, Guid RegionId, Guid PlaceId);
}
