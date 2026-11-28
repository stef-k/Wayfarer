using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.WebUtilities;

namespace Wayfarer.Services.TripMetadataReceipts;

/// <summary>Protects permits with the persistent deployment ring and a versioned database authority purpose.</summary>
internal sealed class TripMetadataProofs(IDataProtectionProvider provider, IKeyManager keys)
{
    private const uint KeyRingHeader = 0x09f0c9f0;

    /// <summary>Issues only when the actual key selected by Data Protection is active and non-revoked.</summary>
    internal string Protect(TripMetadataIdentity identity, Guid authority)
    {
        try
        {
            var bytes = Protector(authority, identity.Contract).Protect(JsonSerializer.SerializeToUtf8Bytes(new BoundPermit(authority, identity)));
            var key = FindKey(bytes);
            var now = DateTimeOffset.UtcNow;
            if (key is { IsRevoked: false } && key.ActivationDate <= now && now < key.ExpirationDate)
                return WebEncoders.Base64UrlEncode(bytes);
        }
        catch (Exception error) when (IsKeyFailure(error)) { }
        throw TripMetadataOperations.Unavailable();
    }

    /// <summary>Verifies retained, non-revoked material even after key expiry and receipt cleanup.</summary>
    internal TripMetadataIdentity Verify(string proof, Guid authority)
    {
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(proof);
            // Consult persisted key metadata even if this node's protection provider has a warm cache.
            if (FindKey(bytes) is { IsRevoked: false })
            {
                return Unprotect(bytes, authority, TripMetadataIdentity.Version)
                    ?? Unprotect(bytes, authority, TripMetadataIdentity.Version2)
                    ?? Unprotect(bytes, authority, TripMetadataIdentity.StructureVersion)
                    ?? throw new CryptographicException("Trip permit version mismatch.");
            }
        }
        catch (Exception error) when (IsKeyFailure(error) || error is FormatException or JsonException) { }
        throw new TripMetadataMutationRejected(409, "trip-attempt-unverifiable", "The Trip save proof cannot be verified.");
    }

    /// <summary>Reads one purpose without letting a v1 verification failure mask a v2 proof.</summary>
    private TripMetadataIdentity? Unprotect(byte[] bytes, Guid authority, string contract)
    {
        try
        {
            var permit = JsonSerializer.Deserialize<BoundPermit>(Protector(authority, contract).Unprotect(bytes));
            return permit?.Authority == authority && permit.Identity.Contract == contract ? permit.Identity : null;
        }
        catch (CryptographicException) { return null; }
    }

    /// <summary>Accepts the documented key-ring payload header and reads the exact emitted key ID.</summary>
    private IKey? FindKey(byte[] bytes)
    {
        if (bytes.Length < 20 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes) != KeyRingHeader)
            return null;
        var id = new Guid(bytes.AsSpan(4, 16));
        return keys.GetAllKeys().SingleOrDefault(key => key.KeyId == id);
    }

    /// <summary>Separates Trip permit formats and continuous database authorities from other protectors.</summary>
    private IDataProtector Protector(Guid authority, string contract) =>
        provider.CreateProtector(contract switch
        {
            TripMetadataIdentity.Version => "Wayfarer.TripMetadata.Permit.v1",
            TripMetadataIdentity.Version2 => "Wayfarer.TripMetadata.Permit.v2",
            TripMetadataIdentity.StructureVersion => "Wayfarer.TripStructure.Permit.v1",
            _ => throw new CryptographicException("Trip permit version mismatch.")
        }, authority.ToString("D"));

    /// <summary>Storage or key-ring failures carry no historical result.</summary>
    private static bool IsKeyFailure(Exception error) =>
        error is CryptographicException or IOException or UnauthorizedAccessException or InvalidOperationException;

    private sealed record BoundPermit(Guid Authority, TripMetadataIdentity Identity);
}
