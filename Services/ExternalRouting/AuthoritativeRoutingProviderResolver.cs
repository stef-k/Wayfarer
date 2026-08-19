using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Resolves exactly one server-authoritative routing mode and its server-only execution data.</summary>
public sealed class AuthoritativeRoutingProviderResolver(
    ApplicationDbContext dbContext, RoutingProviderCredentialService providerCredentials,
    UserRoutingCredentialService userCredentials)
{
    /// <summary>Resolves the authenticated user's current mode for one active transport profile.</summary>
    public async Task<RoutingProviderResolutionResult> ResolveAsync(
        string userId, Guid transportProfileId, CancellationToken cancellationToken)
    {
        var settings = await dbContext.ApplicationSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        if (settings?.ExternalRouteGenerationEnabled != true) return RoutingProviderResolutionResult.Disabled;
        var userConfiguration = await dbContext.Set<UserRoutingConfiguration>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (userConfiguration == null) return RoutingProviderResolutionResult.Unavailable("user-routing-unavailable");
        var personal = userConfiguration.SelectedProviderConfigurationId != null;
        var providerId = personal ? userConfiguration.SelectedProviderConfigurationId
            : settings.ActiveRoutingProviderConfigurationId;
        if (providerId == null) return UnavailableForMode(personal);
        var provider = await dbContext.Set<RoutingProviderConfiguration>().AsNoTracking()
            .Include(item => item.ProfileMappings).ThenInclude(item => item.TransportProfile)
            .SingleOrDefaultAsync(item => item.Id == providerId, cancellationToken);
        if (provider == null) return UnavailableForMode(personal);
        var mapping = provider.ProfileMappings.SingleOrDefault(item => item.TransportProfileId == transportProfileId
            && item.TransportProfile is { IsActive: true } && !string.IsNullOrWhiteSpace(item.OsrmProfile));
        return personal
            ? ResolvePersonal(userConfiguration, provider, mapping, settings.ExternalRouteGenerationVersion)
            : ResolveServerDefault(userConfiguration, provider, mapping, settings.ExternalRouteGenerationVersion);
    }

    private RoutingProviderResolutionResult ResolvePersonal(
        UserRoutingConfiguration userConfiguration, RoutingProviderConfiguration provider,
        RoutingProviderProfileMapping? mapping, int featureVersion)
    {
        if (!PersonalRoutingEligibility.Evaluate(provider).Eligible || mapping == null)
            return RoutingProviderResolutionResult.Unavailable("personal-provider-unavailable");
        string? credential = null;
        if (provider.PersonalRoutingAccess == PersonalRoutingAccess.CredentialFree)
        {
            if (userConfiguration.CredentialPresent || userConfiguration.CredentialCiphertext != null
                || userConfiguration.VerifiedUserConfigurationVersion != null
                || userConfiguration.VerifiedProviderConfigurationVersion != null
                || userConfiguration.VerificationStatus != null)
                return RoutingProviderResolutionResult.Unavailable("personal-configuration-unavailable");
        }
        else
        {
            if (!userConfiguration.CredentialPresent
                || userConfiguration.VerifiedUserConfigurationVersion != userConfiguration.ConfigurationVersion
                || userConfiguration.VerifiedProviderConfigurationVersion != provider.ConfigurationVersion
                || userConfiguration.VerificationStatus != "verified")
                return RoutingProviderResolutionResult.Unavailable("personal-credential-unavailable");
            var read = userCredentials.Unprotect(
                userConfiguration.UserId, provider.Id, userConfiguration.CredentialCiphertext);
            if (!read.Succeeded) return RoutingProviderResolutionResult.Unavailable("personal-credential-unavailable");
            credential = read.Credential;
        }
        return Resolved(RoutingProviderResolutionOutcome.ResolvedPersonal, RoutingProviderSelectionMode.Personal,
            userConfiguration, provider, mapping, featureVersion, credential);
    }

    private RoutingProviderResolutionResult ResolveServerDefault(
        UserRoutingConfiguration userConfiguration, RoutingProviderConfiguration provider,
        RoutingProviderProfileMapping? mapping, int featureVersion)
    {
        if (provider is not { Enabled: true } || provider.VerifiedConfigurationVersion != provider.ConfigurationVersion
            || mapping == null || string.IsNullOrWhiteSpace(provider.BaseEndpoint))
            return RoutingProviderResolutionResult.ServerUnavailable("external-routing-unavailable");
        var credential = providerCredentials.Read(provider);
        if (!credential.Succeeded) return RoutingProviderResolutionResult.ServerUnavailable(credential.ErrorCode!);
        return Resolved(RoutingProviderResolutionOutcome.ServerDefault, RoutingProviderSelectionMode.ServerDefault,
            userConfiguration, provider, mapping, featureVersion, credential.Credential);
    }

    private static RoutingProviderResolutionResult Resolved(
        RoutingProviderResolutionOutcome outcome, RoutingProviderSelectionMode mode,
        UserRoutingConfiguration userConfiguration, RoutingProviderConfiguration provider,
        RoutingProviderProfileMapping mapping, int featureVersion, string? credential)
    {
        var operationalProvider = new RoutingProviderConfiguration
        {
            Id = provider.Id, DisplayName = provider.DisplayName, AdapterType = provider.AdapterType,
            BaseEndpoint = provider.BaseEndpoint, Enabled = provider.Enabled,
            Attribution = provider.Attribution, ExternalCoordinateDisclosure = provider.ExternalCoordinateDisclosure,
            ConfigurationVersion = provider.ConfigurationVersion,
            VerifiedConfigurationVersion = provider.VerifiedConfigurationVersion,
            GenerationTimeoutSeconds = provider.GenerationTimeoutSeconds,
            ResponseSizeLimitBytes = provider.ResponseSizeLimitBytes, RequestsPerMinute = provider.RequestsPerMinute,
            MinimumIntervalMilliseconds = provider.MinimumIntervalMilliseconds, MaxConcurrency = provider.MaxConcurrency
        };
        return new RoutingProviderResolutionResult(outcome, null, false,
            new ResolvedRoutingProviderExecution(
                operationalProvider, mapping.OsrmProfile, credential, mode, userConfiguration.ConfigurationVersion,
                userConfiguration.RowVersion, provider.ConfigurationVersion, provider.RowVersion, featureVersion,
                provider.DisplayName, provider.ExternalCoordinateDisclosure, provider.Attribution));
    }

    private static RoutingProviderResolutionResult UnavailableForMode(bool personal) => personal
        ? RoutingProviderResolutionResult.Unavailable("personal-provider-unavailable")
        : RoutingProviderResolutionResult.ServerUnavailable("external-routing-unavailable");

    /// <summary>Creates a terminal personal-unavailable result.</summary>
    public static RoutingProviderResolutionResult UnavailablePersonal(string errorCode) =>
        RoutingProviderResolutionResult.Unavailable(errorCode);
}

/// <summary>Identifies the authoritative provider-selection outcome.</summary>
public enum RoutingProviderResolutionOutcome { ServerDefault, ResolvedPersonal, UnavailablePersonal, ExternalRoutingDisabled }

/// <summary>Contains a bounded outcome and optional server-only execution authority.</summary>
public sealed record RoutingProviderResolutionResult(
    RoutingProviderResolutionOutcome Outcome, string? ErrorCode, bool MayResolveServerDefault,
    ResolvedRoutingProviderExecution? Execution = null)
{
    /// <summary>Gets the disabled result.</summary>
    public static RoutingProviderResolutionResult Disabled { get; } =
        new(RoutingProviderResolutionOutcome.ExternalRoutingDisabled, "external-routing-disabled", false);
    /// <summary>Creates terminal personal unavailability.</summary>
    public static RoutingProviderResolutionResult Unavailable(string code) =>
        new(RoutingProviderResolutionOutcome.UnavailablePersonal, code, false);
    /// <summary>Creates bounded server-default unavailability without changing mode.</summary>
    public static RoutingProviderResolutionResult ServerUnavailable(string code) =>
        new(RoutingProviderResolutionOutcome.ServerDefault, code, false);
}

/// <summary>Contains immutable server-only execution data; it must never enter a controller response.</summary>
public sealed record ResolvedRoutingProviderExecution(
    RoutingProviderConfiguration Provider, string Profile, string? Credential,
    RoutingProviderSelectionMode SelectionMode, int UserConfigurationVersion, uint UserRowVersion,
    int ProviderConfigurationVersion, uint ProviderRowVersion, int FeatureStateGeneration,
    string DisplayName, string? Disclosure, string? Attribution);

/// <summary>Identifies the provider selection bound into protected proposals.</summary>
public enum RoutingProviderSelectionMode { ServerDefault, Personal }
