using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Resolves personal Geoapify directions authority without administrator configuration.</summary>
public sealed class AuthoritativeRoutingProviderResolver(
    ApplicationDbContext dbContext, PersonalProviderCredentialService personalCredentials)
{
    private static readonly Guid GeoapifyPacingIdentity = Guid.Parse("5bde15a4-984c-4daa-912d-9fa59a166ec3");
    /// <summary>Resolves one explicit provider-native mode from personal authority only.</summary>
    public async Task<RoutingProviderResolutionResult> ResolveNativeAsync(
        string userId, string? nativeMode, CancellationToken cancellationToken)
    {
        if (!ProviderDirectionsCatalog.TryParse("geoapify", nativeMode, out _))
            return RoutingProviderResolutionResult.Unavailable("unsupported-provider-mode");
        return await ResolvePersonalGeoapifyAsync(userId, nativeMode!, cancellationToken);
    }

    /// <summary>Resolves an explicit mode from caller-owned, locked personal-authority rows.</summary>
    public RoutingProviderResolutionResult ResolveLockedNative(
        PersonalLocationProviderSelection? selection, PersonalLocationProviderProfile? profile, string? nativeMode)
    {
        if (!ProviderDirectionsCatalog.TryParse("geoapify", nativeMode, out _))
            return RoutingProviderResolutionResult.Unavailable("unsupported-provider-mode");
        if (selection?.RoutingProviderKey != "geoapify")
            return RoutingProviderResolutionResult.Unavailable("no-provider-selected");
        return ResolveProfile(selection, profile, nativeMode!);
    }

    /// <summary>Resolves the released-Mobile omitted-mode request from an exact built-in stable key.</summary>
    public async Task<RoutingProviderResolutionResult> ResolveReleasedMobileAsync(
        string userId, Guid transportProfileId, CancellationToken cancellationToken)
    {
        var profile = await dbContext.Set<TransportProfile>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == transportProfileId && item.IsActive, cancellationToken);
        if (profile == null || !ReleasedMobileDirectionsCompatibility.TryMap(profile, out var nativeMode))
            return RoutingProviderResolutionResult.Unavailable("unmapped-transport-profile");
        return await ResolvePersonalGeoapifyAsync(userId, nativeMode, cancellationToken);
    }

    private async Task<RoutingProviderResolutionResult> ResolvePersonalGeoapifyAsync(
        string userId, string nativeMode, CancellationToken cancellationToken)
    {
        var selection = await dbContext.Set<PersonalLocationProviderSelection>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (selection?.RoutingProviderKey != "geoapify")
            return RoutingProviderResolutionResult.Unavailable("no-provider-selected");
        var profile = await dbContext.Set<PersonalLocationProviderProfile>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId
                && item.ProviderKey == "geoapify", cancellationToken);
        return ResolveProfile(selection, profile, nativeMode);
    }

    private RoutingProviderResolutionResult ResolveProfile(
        PersonalLocationProviderSelection selection, PersonalLocationProviderProfile? profile, string nativeMode)
    {
        if (profile == null || profile.UserId != selection.UserId
            || profile.ProviderKey != "geoapify" || profile.RevokedAt != null
            || !profile.RoutingAuthorized)
            return RoutingProviderResolutionResult.Unavailable("unauthorized");
        if (profile.RoutingVerification != PersonalProviderVerification.Verified
            || profile.RoutingVerifiedCredentialGeneration != profile.CredentialGeneration
            || profile.RoutingVerifiedConfigurationGeneration != profile.RoutingGeneration)
            return RoutingProviderResolutionResult.Unavailable("verification-required");
        var credential = personalCredentials.Read(profile);
        if (!credential.Succeeded)
            return RoutingProviderResolutionResult.Unavailable("personal-credential-unavailable");

        var execution = new ResolvedRoutingProviderExecution(
            "geoapify", GeoapifyPacingIdentity, nativeMode, credential.Credential!, selection.UserId,
            selection.RoutingSelectionGeneration, profile.CredentialGeneration, profile.RoutingGeneration,
            profile.RoutingAuthorized, profile.RoutingVerification,
            profile.RoutingVerifiedCredentialGeneration, profile.RoutingVerifiedConfigurationGeneration,
            ProviderDirectionsCatalog.AuthorityVersion, "Geoapify",
            "Route coordinates are sent to Geoapify.",
            "Powered by Geoapify|© OpenStreetMap contributors", 30, 2_000_000, 60, 0, 2);
        return new RoutingProviderResolutionResult(null, execution);
    }
}

/// <summary>Contains personal provider execution data that never enters a controller response.</summary>
public sealed record ResolvedRoutingProviderExecution(
    string ProviderKey, Guid PacingIdentity, string Profile, string Credential, string PersonalProviderUserId,
    int AuthoritySelectionGeneration, int CredentialGeneration, int RoutingGeneration,
    bool RoutingAuthorized, PersonalProviderVerification RoutingVerification,
    int? VerifiedCredentialGeneration, int? VerifiedRoutingGeneration, int CatalogVersion,
    string DisplayName, string Disclosure, string Attribution, int TimeoutSeconds, int ResponseSizeLimitBytes,
    int RequestsPerMinute, int MinimumIntervalMilliseconds, int MaxConcurrency);

/// <summary>Contains a bounded personal-provider resolution outcome.</summary>
public sealed record RoutingProviderResolutionResult(string? ErrorCode, ResolvedRoutingProviderExecution? Execution = null)
{
    /// <summary>Creates terminal personal unavailability.</summary>
    public static RoutingProviderResolutionResult Unavailable(string code) => new(code);
}
