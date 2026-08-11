using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Wayfarer.Services;

/// <summary>Protects provider concurrency values for the same-origin Segment editor contract.</summary>
public sealed class SegmentAggregateTokenService
{
    private const int ContractVersion = 1;
    private readonly IDataProtector _protector;

    /// <summary>Creates the dedicated Segment aggregate protector.</summary>
    public SegmentAggregateTokenService(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("Wayfarer.TripEditor.SegmentAggregate.v1");

    /// <summary>Issues a token bound to one user-owned Segment aggregate.</summary>
    public string Issue(string userId, Guid tripId, Guid segmentId, uint rowVersion) =>
        _protector.Protect(JsonSerializer.Serialize(new Payload(ContractVersion, userId, tripId, segmentId, rowVersion)));

    /// <summary>Validates scope and returns the protected provider row version.</summary>
    public bool TryRead(string? token, string userId, Guid tripId, Guid segmentId, out uint rowVersion)
    {
        rowVersion = 0;
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(token));
            if (payload is null || payload.Version != ContractVersion || payload.UserId != userId
                || payload.TripId != tripId || payload.SegmentId != segmentId) return false;
            rowVersion = payload.RowVersion;
            return true;
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return false;
        }
    }

    private sealed record Payload(int Version, string UserId, Guid TripId, Guid SegmentId, uint RowVersion);
}
