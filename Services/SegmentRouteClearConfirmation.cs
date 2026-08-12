using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Wayfarer.Services;

/// <summary>Issues expiring confirmations for destructive editor anchor changes.</summary>
public sealed class SegmentRouteClearConfirmation
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;

    /// <summary>Creates a confirmation codec with a purpose distinct from lifecycle confirmation.</summary>
    public SegmentRouteClearConfirmation(IDataProtectionProvider provider, TimeProvider clock)
    {
        _protector = provider.CreateProtector("Wayfarer.TripEditor.SegmentRouteClear.v1");
        _clock = clock;
    }

    /// <summary>Builds a privacy-safe fingerprint of all state that authorizes route clearing.</summary>
    public static string Fingerprint(string userId, Guid tripId, SegmentRouteClearState current, SegmentRouteClearState proposed)
    {
        var json = JsonSerializer.Serialize(new { userId, tripId, current, proposed });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>Issues an opaque confirmation for the supplied fingerprint.</summary>
    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid segmentId, string fingerprint)
    {
        var expiresAt = _clock.GetUtcNow().Add(Lifetime);
        return (_protector.Protect(JsonSerializer.Serialize(new Payload(1, segmentId, fingerprint, expiresAt))), expiresAt);
    }

    /// <summary>Checks an opaque token without exposing its contents.</summary>
    public bool IsValid(string? token, Guid segmentId, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(token));
            return payload is { Version: 1 } && payload.SegmentId == segmentId
                && payload.Fingerprint == fingerprint && payload.ExpiresAt > _clock.GetUtcNow();
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return false;
        }
    }

    private sealed record Payload(int Version, Guid SegmentId, string Fingerprint, DateTimeOffset ExpiresAt);
}

/// <summary>Canonical state included in a route-clear confirmation fingerprint.</summary>
public sealed record SegmentRouteClearState(
    uint RowVersion,
    Guid? FromPlaceId,
    Guid? ToPlaceId,
    IReadOnlyList<Guid> WaypointPlaceIds,
    Guid? TransportProfileId,
    string GeometryIdentity);
