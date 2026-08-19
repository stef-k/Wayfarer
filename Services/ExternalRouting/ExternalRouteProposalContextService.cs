using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Protects immutable proposal geometry and authoritative generation context.</summary>
public sealed class ExternalRouteProposalContextService
{
    private const int ContractVersion = 1;
    private static readonly TimeSpan ProposalLifetime = TimeSpan.FromMinutes(10);
    private readonly IDataProtector _protector;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes the isolated proposal protector and expiry clock.</summary>
    public ExternalRouteProposalContextService(IDataProtectionProvider provider, TimeProvider? timeProvider = null)
    {
        _protector = provider.CreateProtector("Wayfarer.ExternalRouting.ProposalContext.v1");
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Issues a ten-minute token binding geometry and every authoritative stale dimension.</summary>
    public ProtectedProposalContext Issue(ExternalRouteProposalBinding binding)
    {
        var expiresAt = _timeProvider.GetUtcNow().Add(ProposalLifetime);
        var payload = new ProposalPayload(ContractVersion, binding, expiresAt);
        return new ProtectedProposalContext(_protector.Protect(JsonSerializer.Serialize(payload)), expiresAt);
    }

    /// <summary>Reads an unaltered, unexpired token without contacting the provider.</summary>
    public bool TryRead(string? token, out ExternalRouteProposalBinding? binding)
    {
        binding = null;
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var payload = JsonSerializer.Deserialize<ProposalPayload>(_protector.Unprotect(token));
            if (payload == null || payload.Version != ContractVersion || payload.ExpiresAt <= _timeProvider.GetUtcNow()) return false;
            binding = payload.Binding;
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException) { return false; }
    }

    /// <summary>Computes a canonical hash over geometry and waypoint indices.</summary>
    public static string GeometryHash(IReadOnlyList<RouteCoordinate> geometry, IReadOnlyList<int> waypointIndices)
    {
        var canonical = JsonSerializer.Serialize(new { Geometry = geometry, WaypointIndices = waypointIndices });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed record ProposalPayload(int Version, ExternalRouteProposalBinding Binding, DateTimeOffset ExpiresAt);
}

/// <summary>Contains all server-authoritative proposal stale dimensions.</summary>
public sealed record ExternalRouteProposalBinding(
    Guid ProposalId, Guid TripId, Guid SegmentId, string UserId, string GeometryHash, string AnchorFingerprint,
    Guid TransportProfileId, Guid ProviderId, int ProviderConfigurationVersion, int FeatureStateGeneration,
    string AggregateConcurrencyToken);

/// <summary>Returns the protected context and its initial ten-minute expiry.</summary>
public sealed record ProtectedProposalContext(string Token, DateTimeOffset ExpiresAt);
