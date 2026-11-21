using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Areas.User.LocationProviderModels;
using Wayfarer.Localization;
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
    IImportEnrichmentHandoff? enrichmentCommands = null,
    PersonalProviderSetupService? setup = null) : Controller
{
    /// <summary>Displays read-only masked provider authority and pending legacy migration status.</summary>
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        return View(await BuildAsync(userId, cancellationToken));
    }

    /// <summary>Explicitly evaluates legacy migration for the authenticated owner and reports only bounded status.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MigrateLegacyMapbox(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        var result = await migration.MigrateAsync(userId, cancellationToken);
        return RedirectWithStatus(result.State switch
        {
            LegacyMapboxMigrationState.Migrated => "Migrated",
            LegacyMapboxMigrationState.None => "NoMigration",
            LegacyMapboxMigrationState.Conflict => "MigrationConflict",
            LegacyMapboxMigrationState.ProtectedCredentialUnavailable => "MigrationUnavailable",
            LegacyMapboxMigrationState.Revoked => "MigrationRevoked",
            _ => throw new InvalidOperationException("Unrecognized legacy migration outcome.")
        });
    }

    /// <summary>Replaces a credential only when nonblank and changes explicit capability selections independently.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveProfile(LocationProviderProfileInput input, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        if (!ModelState.IsValid) return View("Index", await BuildAsync(userId, cancellationToken));
        if (setup == null || string.IsNullOrWhiteSpace(input.ReplacementCredential))
            return RedirectToAction(nameof(Index));

        await setup.ReplaceCredentialAsync(
            userId, ParseProvider(input.ProviderKey), input.ReplacementCredential, cancellationToken);
        return RedirectWithStatus("CredentialReplaced");
    }

    /// <summary>Replaces one credential and disables its selections without provider contact.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCredential(LocationProviderCredentialInput input, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        if (!ModelState.IsValid || setup == null) return View("Index", await BuildAsync(userId, cancellationToken));
        await setup.ReplaceCredentialAsync(userId, ParseProvider(input.ProviderKey), input.ReplacementCredential, cancellationToken);
        return RedirectWithStatus("CredentialReplaced");
    }

    /// <summary>Applies one capability provider choice without contacting a provider.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ChooseProvider(LocationProviderChoiceInput input, [FromServices] WebMessages text,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        var capabilityValid = TryParseCapability(input.Capability, out var capability);
        var providerValid = TryParseOptionalProvider(input.ProviderKey, out var provider);
        if (!capabilityValid || !providerValid)
            ModelState.AddModelError(string.Empty, text["Providers.Validation.Capability"]);
        if (!ModelState.IsValid || setup == null) return View("Index", await BuildAsync(userId, cancellationToken));
        var result = await setup.ChooseAsync(userId, capability, provider, cancellationToken);
        return result == ProviderChoiceResult.Success
            ? RedirectWithStatus("ChoiceSaved", capability) : RedirectWithStatus("ChoiceBlocked");
    }

    /// <summary>Records explicit Mapbox Permanent Geocoding consent after every acknowledgement validates.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ConsentMapboxPermanent(MapboxPermanentConsentInput input, [FromServices] WebMessages text,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        if (!ModelState.IsValid) return View("Index", await BuildAsync(userId, cancellationToken));
        var profile = await dbContext.PersonalLocationProviderProfiles.SingleOrDefaultAsync(
            item => item.UserId == userId && item.ProviderKey == "mapbox", cancellationToken);
        if (profile == null || credentials.Read(profile).Succeeded == false)
        {
            ModelState.AddModelError(string.Empty, text["Providers.Validation.ConsentCredential"]);
            return View("Index", await BuildAsync(userId, cancellationToken));
        }
        profile.GrantPermanentGeocodingConsent(DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        return RedirectWithStatus("ConsentRecorded");
    }

    /// <summary>Runs one explicit admitted Mapbox Permanent verification contact.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyMapboxPermanent(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        if (setup == null || !await setup.AuthorizeVerificationAsync(userId, PersonalLocationProvider.Mapbox,
            PersonalProviderCapability.Geocoding, cancellationToken))
            return RedirectWithStatus("MapboxNeedsSetup");
        return RedirectWithStatus(await reverseGeocoding.VerifyMapboxPermanentAsync(userId, cancellationToken) switch
        {
            PersonalProviderVerification.Verified => "MapboxVerified",
            PersonalProviderVerification.Failed => "MapboxRejected",
            _ => "MapboxVerifyUnavailable"
        });
    }

    /// <summary>Runs one explicit Geoapify capability verification without changing selection.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyGeoapify(PersonalProviderCapability capability, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        if (capability is not (PersonalProviderCapability.Geocoding or PersonalProviderCapability.Routing))
            return RedirectWithStatus("GeoapifyInvalidCapability");
        if (setup == null || !await setup.AuthorizeVerificationAsync(
            userId, PersonalLocationProvider.Geoapify, capability, cancellationToken))
            return RedirectWithStatus("GeoapifyNeedsCredential");
        var result = geoapifyVerification == null
            ? new GeoapifyVerificationOutcome(PersonalProviderVerification.Unavailable, GeoapifyVerificationCategory.TemporaryFailure)
            : capability == PersonalProviderCapability.Geocoding
                ? await geoapifyVerification.VerifyGeocodingAsync(userId, cancellationToken)
                : await geoapifyVerification.VerifyRoutingAsync(userId, cancellationToken);
        return RedirectWithStatus(GeoapifyVerificationStatus(result.Category), capability);
    }

    /// <summary>Runs one explicit bounded Location backfill for the authenticated owner.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> BackfillGeoapify(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        if (enrichmentCommands == null) return RedirectWithStatus("BackfillUnavailable");
        var result = await enrichmentCommands.StartAsync(userId, cancellationToken);
        return RedirectWithStatus(result.Succeeded ? "BackfillScheduled" : result.Code switch
        {
            "invalid-state" => "BackfillReview",
            "authority-unavailable" => "BackfillAuthority",
            "no-candidates" => "BackfillEmpty",
            "stale-control" or "concurrent-command" => "BackfillChanged",
            "scheduling-reconciliation-required" => "BackfillUnconfirmed",
            _ => "BackfillUnavailable"
        });
    }

    /// <summary>Explicitly revokes one credential without deleting profiles, usage, or domain data.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(string providerKey, bool confirmed, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        if (!confirmed) return RedirectWithStatus("RevokeRequired");
        var profile = await dbContext.PersonalLocationProviderProfiles.SingleOrDefaultAsync(
            item => item.UserId == userId && item.ProviderKey == providerKey, cancellationToken);
        if (profile == null) return RedirectWithStatus("RevokeMissing");
        credentials.Revoke(profile);
        await dbContext.SaveChangesAsync(cancellationToken);
        return RedirectWithStatus("Revoked");
    }

    /// <summary>Updates only a provider-native guard; lowering never deletes or resets usage.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveGuard(LocationProviderGuardInput input, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        if (!ModelState.IsValid) return RedirectWithStatus("GuardInvalid");
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
        return RedirectWithStatus("GuardSaved");
    }

    /// <summary>Builds executable eligibility and masked status without provider contact or mutation.</summary>
    internal async Task<LocationProviderSettingsViewModel> BuildAsync(string userId, CancellationToken cancellationToken)
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
        var activeGeocoding = selection?.GeocodingProviderKey;
        var activeRouting = selection?.RoutingProviderKey;
        return new()
        {
            HasLegacyMapboxRows = await migration.HasLegacyRowsAsync(userId, cancellationToken),
            Profiles = views, ActiveGeocodingProvider = activeGeocoding,
            ActiveRoutingProvider = activeRouting,
            GeocodingStatus = SelectionStatus(activeGeocoding, views, PersonalProviderCapability.Geocoding),
            RoutingStatus = SelectionStatus(activeRouting, views, PersonalProviderCapability.Routing),
            LegacyMigrationState = profiles.SingleOrDefault(item => item.ProviderKey == "mapbox")?.LegacyMigrationState ?? LegacyMapboxMigrationState.None
        };
    }

    private LocationProviderProfileViewModel BuildProfile(PersonalLocationProvider provider,
        IReadOnlyCollection<PersonalLocationProviderProfile> profiles, GeoapifyUsageGuard? geoGuard, int geoUsed,
        IReadOnlyCollection<MapboxProductMeter> meters)
    {
        var key = PersonalProviderKeys.Key(provider);
        var profile = profiles.SingleOrDefault(item => item.ProviderKey == key);
        var readable = profile != null && credentials.Read(profile).Succeeded;
        var geocoding = PersonalProviderEligibility.Evaluate(
            profile, provider, PersonalProviderCapability.Geocoding, readable);
        var routing = PersonalProviderEligibility.Evaluate(
            profile, provider, PersonalProviderCapability.Routing, readable);
        if (provider == PersonalLocationProvider.Geoapify)
        {
            var limit = geoGuard?.CreditLimit ?? 2500;
            return new(key, "Geoapify", readable, "••••••••••••••••",
                profile?.GeocodingAuthorized == true, CurrentVerification(profile, PersonalProviderCapability.Geocoding),
                geocoding.Eligible, geocoding.Status,
                profile?.RoutingAuthorized == true, CurrentVerification(profile, PersonalProviderCapability.Routing),
                routing.Eligible, routing.Status,
                geoGuard?.Enabled ?? true, limit, geoUsed, "credits",
                "Wayfarer rolling 24-hour shared geocoding/routing window", (geoGuard?.Enabled ?? true) && geoUsed >= limit);
        }
        var permanent = meters.SingleOrDefault(item => item.Product == PersonalProviderProduct.PermanentGeocoding);
        var directions = meters.SingleOrDefault(item => item.Product == PersonalProviderProduct.Directions);
        return new(key, "Mapbox", readable, "••••••••••••••••",
            profile?.GeocodingAuthorized == true, CurrentVerification(profile, PersonalProviderCapability.Geocoding),
            geocoding.Eligible, geocoding.Status,
            profile?.RoutingAuthorized == true, CurrentVerification(profile, PersonalProviderCapability.Routing),
            routing.Eligible, routing.Status,
            permanent?.Enabled ?? true, permanent?.Limit ?? 1000, permanent?.AdmittedCount ?? 0, "Permanent Geocoding contacts",
            "Wayfarer UTC calendar-month Permanent Geocoding safety cycle", permanent?.Enabled == true && permanent.AdmittedCount >= permanent.Limit,
            directions?.Enabled ?? true, directions?.Limit ?? 1000, directions?.AdmittedCount ?? 0,
            profile?.HasCurrentPermanentGeocodingConsent() == true, profile?.PermanentGeocodingConsentVersion,
            profile?.PermanentGeocodingConsentedAt, MapboxPausedReason(profile, permanent, geocoding), permanent?.CycleStart);
    }

    private static string SelectionStatus(string? selectedProvider,
        IReadOnlyCollection<LocationProviderProfileViewModel> profiles, PersonalProviderCapability capability)
    {
        if (selectedProvider == null) return capability == PersonalProviderCapability.Geocoding
            ? "No provider selected. Verify a credential, then choose it."
            : "No provider selected. Verify Geoapify, then choose it.";
        var profile = profiles.SingleOrDefault(item => item.ProviderKey == selectedProvider);
        var eligible = capability == PersonalProviderCapability.Geocoding
            ? profile?.GeocodingEligible == true : profile?.RoutingEligible == true;
        if (eligible) return $"Ready with {selectedProvider}.";
        var blocking = capability == PersonalProviderCapability.Geocoding
            ? profile?.GeocodingBlockingStatus : profile?.RoutingBlockingStatus;
        return blocking ?? "Blocked. Replace the credential and verify again.";
    }

    private static PersonalProviderVerification CurrentVerification(
        PersonalLocationProviderProfile? profile, PersonalProviderCapability capability)
    {
        if (profile == null) return PersonalProviderVerification.Unverified;
        return capability == PersonalProviderCapability.Geocoding
            && profile.GeocodingVerifiedCredentialGeneration == profile.CredentialGeneration
            && profile.GeocodingVerifiedConfigurationGeneration == profile.GeocodingGeneration
            ? profile.GeocodingVerification
            : capability == PersonalProviderCapability.Routing
              && profile.RoutingVerifiedCredentialGeneration == profile.CredentialGeneration
              && profile.RoutingVerifiedConfigurationGeneration == profile.RoutingGeneration
                ? profile.RoutingVerification : PersonalProviderVerification.Unverified;
    }

    private static string? MapboxPausedReason(PersonalLocationProviderProfile? profile, MapboxProductMeter? meter,
        PersonalProviderEligibilityResult eligibility) => profile switch
    { _ when !eligibility.Eligible => eligibility.Status, _ when meter?.Enabled == true && meter.AdmittedCount >= meter.Limit => "Permanent meter exhausted", _ => null };

    private static PersonalLocationProvider ParseProvider(string key) => key switch
    { "geoapify" => PersonalLocationProvider.Geoapify, "mapbox" => PersonalLocationProvider.Mapbox, _ => throw new ArgumentOutOfRangeException(nameof(key)) };

    /// <summary>Accepts only the two capability values posted by this settings page.</summary>
    private static bool TryParseCapability(string value, out PersonalProviderCapability capability)
    {
        capability = value switch
        {
            "Geocoding" => PersonalProviderCapability.Geocoding,
            "Routing" => PersonalProviderCapability.Routing,
            _ => default
        };
        return value is "Geocoding" or "Routing";
    }

    /// <summary>Accepts no provider or one exact supported provider key.</summary>
    private static bool TryParseOptionalProvider(string value, out PersonalLocationProvider? provider)
    {
        provider = value switch
        {
            "geoapify" => PersonalLocationProvider.Geoapify,
            "mapbox" => PersonalLocationProvider.Mapbox,
            _ => null
        };
        return string.IsNullOrEmpty(value) || provider != null;
    }

    /// <summary>Maps bounded verification detail to a credential-free status identity.</summary>
    private static string GeoapifyVerificationStatus(GeoapifyVerificationCategory category) => category switch
    {
        GeoapifyVerificationCategory.Verified => "GeoVerified",
        GeoapifyVerificationCategory.AuthorizationDisabled => "GeoAuthorizationDisabled",
        GeoapifyVerificationCategory.CredentialUnavailable => "GeoCredentialUnavailable",
        GeoapifyVerificationCategory.GuardExhausted => "GeoGuardExhausted",
        GeoapifyVerificationCategory.ProviderRejected => "GeoProviderRejected",
        GeoapifyVerificationCategory.RateLimited => "GeoRateLimited",
        GeoapifyVerificationCategory.TemporaryFailure => "GeoTemporaryFailure",
        GeoapifyVerificationCategory.InvalidResponse => "GeoInvalidResponse",
        _ => "GeoAuthorityChanged"
    };

    /// <summary>Stores a catalog key and, when required, a fixed capability value for the destination GET.</summary>
    private IActionResult RedirectWithStatus(string status, PersonalProviderCapability? capability = null)
    {
        TempData["ProviderStatus"] = "Providers.Status." + status;
        TempData["ProviderStatusCapability"] = capability?.ToString();
        return RedirectToAction(nameof(Index));
    }
}
