using System.Security.Cryptography;
using System.Text;
using Wayfarer.Models;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Creates the canonical ordered saved-place identity and coordinate fingerprint.</summary>
public static class ExternalRouteAnchorFingerprint
{
    /// <summary>Hashes ordered place identities and exact authoritative coordinates.</summary>
    public static string Compute(IReadOnlyList<Place?> places, IReadOnlyList<RouteCoordinate> anchors)
    {
        var canonical = string.Join('|', places.Zip(anchors, (place, coordinate) =>
            $"{place!.Id:N}:{coordinate.Longitude:R},{coordinate.Latitude:R}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
