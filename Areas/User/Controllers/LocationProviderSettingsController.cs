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
    IImportEnrichmentHandoff? enrichmentCommands = null,
    PersonalProviderSetupService? setup = null) : Controller
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
        if (setup != null && !string.IsNullOrWhiteSpace(input.ReplacementCredential))
            await setup.ReplaceCredentialAsync(
                userId, ParseProvider(input.ProviderKey), input.ReplacementCredential, cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Replaces one credential and disables its selections without provider contact.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCredential(LocationProviderCredentialInput input, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        if (!ModelState.IsValid || setup == null) return View("Index", await BuildAsync(userId, cancellationToken));
        await setup.ReplaceCredentialAsync(userId, ParseProvider(input.ProviderKey), input.ReplacementCredential, cancellationToken);
        TempData["ProviderStatus"] = "Credential replaced. Existing provider choices remain blocked until each capability is verified again.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Applies one capability provider choice without contacting a provider.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ChooseProvider(LocationProviderChoiceInput input, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        if (!ModelState.IsValid || setup == null) return View("Index", await BuildAsync(userId, cancellationToken));
        var capability = Enum.Parse<PersonalProviderCapability>(input.Capability);
        var provider = string.IsNullOrEmpty(input.ProviderKey) ? (PersonalLocationProvider?)null : ParseProvider(input.ProviderKey);
        var result = await setup.ChooseAsync(userId, capability, provider, cancellationToken);
        TempData["ProviderStatus"] = result == ProviderChoiceResult.Success
            ? $"{capability} provider choice saved." : "Verify this provider for the capability before selecting it.";
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
        if (setup == null || !await setup.AuthorizeVerificationAsync(userId, PersonalLocationProvider.Mapbox,
            PersonalProviderCapability.Geocoding, cancellationToken))
        { TempData["ProviderStatus"] = "Configure a readable credential and current Permanent acknowledgements before verification."; return RedirectToAction(nameof(Index)); }
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
        if (capability is not (PersonalProviderCapability.Geocoding or PersonalProviderCapability.Routing))
        { TempData["ProviderStatus"] = "Choose geocoding or directions to verify."; return RedirectToAction(nameof(Index)); }
        if (setup == null || !await setup.AuthorizeVerificationAsync(
            userId, PersonalLocationProvider.Geoapify, capability, cancellationToken))
        { TempData["ProviderStatus"] = "Configure a readable Geoapify credential before verification."; return RedirectToAction(nameof(Index)); }
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
