using Wayfarer.Models;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Applies the server-owned feature gate before future provider contact.</summary>
public sealed class ExternalRouteProposalGenerator
{
    private readonly Func<ApplicationSettings> _settings;

    /// <summary>Initializes the initial feature-gate seam.</summary>
    public ExternalRouteProposalGenerator(Func<ApplicationSettings> settings) => _settings = settings;

    /// <summary>Rejects disabled generation without contacting a provider.</summary>
    public Task<ExternalRouteGenerationResult> GenerateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_settings().ExternalRouteGenerationEnabled
            ? new ExternalRouteGenerationResult(false, "external-routing-unavailable")
            : new ExternalRouteGenerationResult(false, "external-routing-disabled"));
    }
}

/// <summary>Represents a bounded Wayfarer-owned generation result.</summary>
public sealed record ExternalRouteGenerationResult(bool Succeeded, string? ErrorCode);
