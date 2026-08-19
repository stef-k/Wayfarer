using Wayfarer.Models;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Evaluates administrator-owned personal-template eligibility.</summary>
public static class PersonalRoutingEligibility
{
    /// <summary>Evaluates the current provider and active mapping authority.</summary>
    public static PersonalRoutingEligibilityResult Evaluate(RoutingProviderConfiguration provider)
    {
        var credentialRequired = provider.PersonalRoutingAccess == PersonalRoutingAccess.CredentialRequired;
        var endpointValid = Uri.TryCreate(provider.BaseEndpoint, UriKind.Absolute, out var endpoint)
            && endpoint.Scheme is "https" or "http" && endpoint.UserInfo.Length == 0
            && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0
            && !endpoint.Host.Contains('*', StringComparison.Ordinal);
        var eligible = provider.PersonalRoutingAccess != PersonalRoutingAccess.Disabled
            && provider.Enabled && provider.VerifiedConfigurationVersion == provider.ConfigurationVersion
            && endpointValid
            && !string.IsNullOrWhiteSpace(provider.Attribution)
            && !string.IsNullOrWhiteSpace(provider.ExternalCoordinateDisclosure)
            && provider.ProfileMappings.Any(mapping => mapping.TransportProfile is { IsActive: true }
                && !string.IsNullOrWhiteSpace(mapping.OsrmProfile));
        return new(eligible, credentialRequired);
    }
}

/// <summary>Contains bounded template eligibility and credential mode.</summary>
public sealed record PersonalRoutingEligibilityResult(bool Eligible, bool CredentialRequired);
