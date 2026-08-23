using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Areas.User.LocationProviderModels;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.Areas.User.Controllers;

/// <summary>Manages only the authenticated user's protected provider profiles, selections, and safety guards.</summary>
[Area("User"), Authorize(Roles = "User")]
public sealed class LocationProviderSettingsController(
    ApplicationDbContext dbContext, PersonalProviderCredentialService credentials,
    LegacyMapboxMigrationService migration) : Controller
{
    /// <summary>Displays masked provider authority and provider-native usage status.</summary>
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        await migration.MigrateAsync(userId, cancellationToken);
        return View(await BuildAsync(userId, cancellationToken));
    }

    /// <summary>Replaces a credential only when nonblank and changes explicit capability selections independently.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveProfile(LocationProviderProfileInput input, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        if (!ModelState.IsValid) return View("Index", await BuildAsync(userId, cancellationToken));
        var provider = ParseProvider(input.ProviderKey);
        var key = PersonalProviderKeys.Key(provider);
        var profile = await dbContext.PersonalLocationProviderProfiles
            .SingleOrDefaultAsync(item => item.UserId == userId && item.ProviderKey == key, cancellationToken)
            ?? PersonalLocationProviderProfile.Create(userId, provider);
        if (dbContext.Entry(profile).State == EntityState.Detached) dbContext.Add(profile);
        if (!string.IsNullOrWhiteSpace(input.ReplacementCredential)) credentials.Replace(profile, input.ReplacementCredential);
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, input.GeocodingAuthorized);
        profile.SetAuthorization(PersonalProviderCapability.Routing, input.RoutingAuthorized);

        var selection = await dbContext.PersonalLocationProviderSelections.SingleOrDefaultAsync(
            item => item.UserId == userId, cancellationToken) ?? PersonalLocationProviderSelection.Create(userId);
        if (dbContext.Entry(selection).State == EntityState.Detached) dbContext.Add(selection);
        if (input.ActiveForGeocoding && input.GeocodingAuthorized) selection.Select(PersonalProviderCapability.Geocoding, provider);
        else if (selection.GeocodingProviderKey == key) selection.Select(PersonalProviderCapability.Geocoding, null);
        if (input.ActiveForRouting && input.RoutingAuthorized) selection.Select(PersonalProviderCapability.Routing, provider);
        else if (selection.RoutingProviderKey == key) selection.Select(PersonalProviderCapability.Routing, null);
        await dbContext.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Explicitly revokes one credential without deleting profiles, usage, or domain data.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(string providerKey, bool confirmed, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        if (!confirmed) return RedirectToAction(nameof(Index));
        var profile = await dbContext.PersonalLocationProviderProfiles.SingleOrDefaultAsync(
            item => item.UserId == userId && item.ProviderKey == providerKey, cancellationToken);
        if (profile != null) { credentials.Revoke(profile); await dbContext.SaveChangesAsync(cancellationToken); }
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Updates only a provider-native guard; lowering never deletes or resets usage.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveGuard(LocationProviderGuardInput input, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        if (!ModelState.IsValid) return RedirectToAction(nameof(Index));
        if (input.GuardKey == "geoapify")
        {
            var guard = await dbContext.GeoapifyUsageGuards.SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken)
                ?? new GeoapifyUsageGuard { UserId = userId };
            if (dbContext.Entry(guard).State == EntityState.Detached) dbContext.Add(guard);
            guard.Enabled = input.Enabled; guard.CreditLimit = input.Limit;
        }
        else
        {
            var product = input.GuardKey == "mapbox-permanent"
                ? PersonalProviderProduct.PermanentGeocoding : PersonalProviderProduct.Directions;
            var meter = await dbContext.MapboxProductMeters.SingleOrDefaultAsync(
                item => item.UserId == userId && item.Product == product, cancellationToken)
                ?? new MapboxProductMeter { UserId = userId, Product = product, CycleStart = new(1970, 1, 1) };
            if (dbContext.Entry(meter).State == EntityState.Detached) dbContext.Add(meter);
            meter.Enabled = input.Enabled; meter.Limit = input.Limit;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    private async Task<LocationProviderSettingsViewModel> BuildAsync(string userId, CancellationToken cancellationToken)
    {
        var profiles = await dbContext.PersonalLocationProviderProfiles.AsNoTracking()
            .Where(item => item.UserId == userId).ToListAsync(cancellationToken);
        var selection = await dbContext.PersonalLocationProviderSelections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        var geoGuard = await dbContext.GeoapifyUsageGuards.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        var geoUsed = await dbContext.GeoapifyUsageAdmissions.AsNoTracking()
            .Where(item => item.UserId == userId && item.AdmittedAt > cutoff).SumAsync(item => (int?)item.Credits, cancellationToken) ?? 0;
        var meters = await dbContext.MapboxProductMeters.AsNoTracking().Where(item => item.UserId == userId).ToListAsync(cancellationToken);
        var views = new[] { PersonalLocationProvider.Geoapify, PersonalLocationProvider.Mapbox }.Select(provider =>
            BuildProfile(provider, profiles, geoGuard, geoUsed, meters)).ToArray();
        return new()
        {
            Profiles = views, ActiveGeocodingProvider = selection?.GeocodingProviderKey,
            ActiveRoutingProvider = selection?.RoutingProviderKey,
            LegacyMigrationState = profiles.SingleOrDefault(item => item.ProviderKey == "mapbox")?.LegacyMigrationState ?? LegacyMapboxMigrationState.None
        };
    }

    private static LocationProviderProfileViewModel BuildProfile(PersonalLocationProvider provider,
        IReadOnlyCollection<PersonalLocationProviderProfile> profiles, GeoapifyUsageGuard? geoGuard, int geoUsed,
        IReadOnlyCollection<MapboxProductMeter> meters)
    {
        var key = PersonalProviderKeys.Key(provider);
        var profile = profiles.SingleOrDefault(item => item.ProviderKey == key);
        if (provider == PersonalLocationProvider.Geoapify)
        {
            var limit = geoGuard?.CreditLimit ?? 2500;
            return new(key, "Geoapify", profile?.ProtectedCredential != null && profile.RevokedAt == null, "••••••••••••••••",
                profile?.GeocodingAuthorized == true, profile?.GeocodingVerification ?? 0,
                profile?.RoutingAuthorized == true, profile?.RoutingVerification ?? 0,
                geoGuard?.Enabled ?? true, limit, geoUsed, "credits",
                "Wayfarer rolling 24-hour shared geocoding/routing window", (geoGuard?.Enabled ?? true) && geoUsed >= limit);
        }
        var permanent = meters.SingleOrDefault(item => item.Product == PersonalProviderProduct.PermanentGeocoding);
        var directions = meters.SingleOrDefault(item => item.Product == PersonalProviderProduct.Directions);
        return new(key, "Mapbox", profile?.ProtectedCredential != null && profile.RevokedAt == null, "••••••••••••••••",
            profile?.GeocodingAuthorized == true, profile?.GeocodingVerification ?? 0,
            profile?.RoutingAuthorized == true, profile?.RoutingVerification ?? 0,
            permanent?.Enabled ?? true, permanent?.Limit ?? 1000, permanent?.AdmittedCount ?? 0, "Permanent Geocoding contacts",
            "Wayfarer UTC calendar-month Permanent Geocoding safety cycle", permanent?.Enabled == true && permanent.AdmittedCount >= permanent.Limit,
            directions?.Enabled ?? true, directions?.Limit ?? 1000, directions?.AdmittedCount ?? 0);
    }

    private static PersonalLocationProvider ParseProvider(string key) => key switch
    { "geoapify" => PersonalLocationProvider.Geoapify, "mapbox" => PersonalLocationProvider.Mapbox, _ => throw new ArgumentOutOfRangeException(nameof(key)) };
}
