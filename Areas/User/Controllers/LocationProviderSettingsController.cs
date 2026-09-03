using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Areas.User.LocationProviderModels;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationEnrichment;

namespace Wayfarer.Areas.User.Controllers;

/// <summary>Manages only the authenticated user's protected provider profiles, selections, and safety guards.</summary>
[Area("User"), Authorize(Roles = "User")]
public sealed class LocationProviderSettingsController(
    ApplicationDbContext dbContext, PersonalProviderCredentialService credentials,
    LegacyMapboxMigrationService migration, ReverseGeocodingService reverseGeocoding,
    GeoapifyVerificationService? geoapifyVerification = null,
    IImportEnrichmentHandoff? enrichmentCommands = null) : Controller
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
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        var selection = dbContext.Database.IsNpgsql()
            ? await dbContext.PersonalLocationProviderSelections.FromSqlInterpolated($$"""
                SELECT *, xmin FROM "PersonalLocationProviderSelections"
                WHERE "UserId" = {{userId}} FOR UPDATE
                """).SingleOrDefaultAsync(cancellationToken)
            : await dbContext.PersonalLocationProviderSelections.SingleOrDefaultAsync(
                item => item.UserId == userId, cancellationToken);
        selection ??= PersonalLocationProviderSelection.Create(userId);
        if (dbContext.Entry(selection).State == EntityState.Detached) dbContext.Add(selection);
        var profile = dbContext.Database.IsNpgsql()
            ? await dbContext.PersonalLocationProviderProfiles.FromSqlInterpolated($$"""
                SELECT *, xmin FROM "PersonalLocationProviderProfiles"
                WHERE "UserId" = {{userId}} AND "ProviderKey" = {{key}} FOR UPDATE
                """).SingleOrDefaultAsync(cancellationToken)
            : await dbContext.PersonalLocationProviderProfiles.SingleOrDefaultAsync(
                item => item.UserId == userId && item.ProviderKey == key, cancellationToken);
        profile ??= PersonalLocationProviderProfile.Create(userId, provider);
        if (dbContext.Entry(profile).State == EntityState.Detached) dbContext.Add(profile);
        if (!string.IsNullOrWhiteSpace(input.ReplacementCredential)) credentials.Replace(profile, input.ReplacementCredential);
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, input.GeocodingAuthorized);
        profile.SetAuthorization(PersonalProviderCapability.Routing, input.RoutingAuthorized);
        var geocodingVerified = profile.GeocodingVerification == PersonalProviderVerification.Verified
            && profile.GeocodingVerifiedCredentialGeneration == profile.CredentialGeneration
            && profile.GeocodingVerifiedConfigurationGeneration == profile.GeocodingGeneration;
        if (input.ActiveForGeocoding && input.GeocodingAuthorized && geocodingVerified
            && (provider != PersonalLocationProvider.Mapbox || profile.HasCurrentPermanentGeocodingConsent()))
            selection.Select(PersonalProviderCapability.Geocoding, provider);
        else if (selection.GeocodingProviderKey == key) selection.Select(PersonalProviderCapability.Geocoding, null);
        var routingVerified = profile.RoutingVerification == PersonalProviderVerification.Verified
            && profile.RoutingVerifiedCredentialGeneration == profile.CredentialGeneration
            && profile.RoutingVerifiedConfigurationGeneration == profile.RoutingGeneration;
        if (input.ActiveForRouting && input.RoutingAuthorized && routingVerified)
            selection.Select(PersonalProviderCapability.Routing, provider);
        else if (selection.RoutingProviderKey == key) selection.Select(PersonalProviderCapability.Routing, null);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Records explicit Mapbox Permanent Geocoding consent after every acknowledgement validates.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ConsentMapboxPermanent(MapboxPermanentConsentInput input, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        if (!ModelState.IsValid) return View("Index", await BuildAsync(userId, cancellationToken));
        var profile = await dbContext.PersonalLocationProviderProfiles.SingleOrDefaultAsync(
            item => item.UserId == userId && item.ProviderKey == "mapbox", cancellationToken);
        if (profile == null || credentials.Read(profile).Succeeded == false)
        { ModelState.AddModelError(string.Empty, "Configure a readable Mapbox credential before consenting."); return View("Index", await BuildAsync(userId, cancellationToken)); }
        profile.GrantPermanentGeocodingConsent(DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Runs one explicit admitted Mapbox Permanent verification contact.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyMapboxPermanent(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        TempData["ProviderStatus"] = await reverseGeocoding.VerifyMapboxPermanentAsync(userId, cancellationToken) switch
        { PersonalProviderVerification.Verified => "Mapbox Permanent Geocoding verified. Activate it explicitly to begin enrichment.", PersonalProviderVerification.Failed => "Mapbox rejected Permanent Geocoding authorization.", _ => "Mapbox Permanent Geocoding verification is unavailable; no stored data was changed." };
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Runs one explicit Geoapify capability verification without changing selection.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyGeoapify(PersonalProviderCapability capability, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        var result = geoapifyVerification == null
            ? new GeoapifyVerificationOutcome(PersonalProviderVerification.Unavailable, GeoapifyVerificationCategory.TemporaryFailure)
            : capability == PersonalProviderCapability.Geocoding
                ? await geoapifyVerification.VerifyGeocodingAsync(userId, cancellationToken)
                : await geoapifyVerification.VerifyRoutingAsync(userId, cancellationToken);
        TempData["ProviderStatus"] = GeoapifyVerificationMessage(capability, result);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Runs one explicit bounded Location backfill for the authenticated owner.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> BackfillGeoapify(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        if (enrichmentCommands == null) return RedirectToAction(nameof(Index));
        var result = await enrichmentCommands.StartAsync(userId, cancellationToken);
        TempData["ProviderStatus"] = result.Succeeded
            ? "Missing-address enrichment is scheduled through the durable workflow."
            : $"Missing-address enrichment was not started: {result.Code}.";
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
            directions?.Enabled ?? true, directions?.Limit ?? 1000, directions?.AdmittedCount ?? 0,
            profile?.HasCurrentPermanentGeocodingConsent() == true, profile?.PermanentGeocodingConsentVersion,
            profile?.PermanentGeocodingConsentedAt, MapboxPausedReason(profile, permanent), permanent?.CycleStart);
    }

    private static string? MapboxPausedReason(PersonalLocationProviderProfile? profile, MapboxProductMeter? meter) => profile switch
    { null or { ProtectedCredential: null } => "Credential required", { GeocodingAuthorized: false } => "Geocoding authorization required", _ when !profile.HasCurrentPermanentGeocodingConsent() => "Permanent consent required", { GeocodingVerification: not PersonalProviderVerification.Verified } => "Permanent verification required", _ when meter?.Enabled == true && meter.AdmittedCount >= meter.Limit => "Permanent meter exhausted", _ => null };

    private static PersonalLocationProvider ParseProvider(string key) => key switch
    { "geoapify" => PersonalLocationProvider.Geoapify, "mapbox" => PersonalLocationProvider.Mapbox, _ => throw new ArgumentOutOfRangeException(nameof(key)) };

    /// <summary>Maps bounded request-local verification detail to credential-free presentation.</summary>
    internal static string GeoapifyVerificationMessage(PersonalProviderCapability capability, GeoapifyVerificationOutcome outcome)
    {
        var detail = outcome.Category switch
        {
            GeoapifyVerificationCategory.Verified => "verified successfully",
            GeoapifyVerificationCategory.AuthorizationDisabled => "authorization is not enabled",
            GeoapifyVerificationCategory.CredentialUnavailable => "the credential is missing, revoked, or unreadable",
            GeoapifyVerificationCategory.GuardExhausted => "the local Wayfarer usage guard is exhausted",
            GeoapifyVerificationCategory.ProviderRejected => "the provider rejected the credential or capability",
            GeoapifyVerificationCategory.RateLimited => "the provider rejected the request because of rate or allowance limits",
            GeoapifyVerificationCategory.TemporaryFailure => "the provider or network is temporarily unavailable",
            GeoapifyVerificationCategory.InvalidResponse => "the provider response was invalid or incompatible",
            _ => "authority changed while verification was in progress"
        };
        return $"Geoapify {capability.ToString().ToLowerInvariant()} verification {detail}. No provider was selected automatically.";
    }
}
