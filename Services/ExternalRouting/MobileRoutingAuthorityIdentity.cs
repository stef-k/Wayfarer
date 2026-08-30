using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Wayfarer.Models;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Encodes the versioned, non-secret Mobile routing-authority change detector.</summary>
public static class MobileRoutingAuthorityIdentity
{
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("Wayfarer.MobileRoutingAuthority");

    /// <summary>Returns whether a supplied identity has the exact current wire syntax.</summary>
    public static bool IsValid(string? value) => value is { Length: 46 }
        && value.StartsWith("v1.", StringComparison.Ordinal)
        && value.AsSpan(3).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") < 0;

    /// <summary>Computes the opaque v1 identity for one complete authority projection.</summary>
    public static string Compute(MobileRoutingAuthorityProjection value) =>
        "v1." + Convert.ToBase64String(SHA256.HashData(Encode(value))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Encodes canonical v1 bytes. Exposed internally for literal vector verification.</summary>
    internal static byte[] Encode(MobileRoutingAuthorityProjection value)
    {
        using var stream = new MemoryStream();
        stream.Write(Domain); stream.WriteByte(0); stream.WriteByte(1);
        Bool(stream, 0x10, value.FeatureEnabled); Int64(stream, 0x11, value.FeatureGeneration);
        stream.WriteByte(0x12); stream.WriteByte(value.AuthorityKind);
        NullableString(stream, 0x13, value.PersonalProviderKey);
        NullableInt64(stream, 0x14, value.SelectionGeneration);
        NullableGuid(stream, 0x15, value.PersonalProfileId);
        NullableBool(stream, 0x16, value.RoutingAuthorized);
        NullableInt64(stream, 0x17, value.RoutingGeneration);
        NullableInt64(stream, 0x18, value.CredentialGeneration);
        NullableInt32(stream, 0x19, value.RoutingVerification);
        NullableInt64(stream, 0x1a, value.VerifiedCredentialGeneration);
        NullableInt64(stream, 0x1b, value.VerifiedConfigurationGeneration);
        NullableGuid(stream, 0x1c, value.SelectedProviderId);
        NullableInt64(stream, 0x1d, value.UserConfigurationVersion);
        NullableBool(stream, 0x1e, value.CredentialPresent);
        NullableInt64(stream, 0x1f, value.VerifiedUserVersion);
        NullableInt64(stream, 0x20, value.VerifiedProviderVersion);
        NullableString(stream, 0x21, value.VerificationStatus);
        Bool(stream, 0x22, value.CredentialReadable);
        Guid(stream, 0x23, value.ProviderId); Bool(stream, 0x24, value.ProviderEnabled);
        Int32(stream, 0x25, value.Adapter); Int64(stream, 0x26, value.ProviderConfigurationVersion);
        NullableInt64(stream, 0x27, value.ProviderVerifiedVersion);
        Int32(stream, 0x28, value.PersonalAccess);
        stream.WriteByte(0x29); UInt32(stream, (uint)value.Profiles.Count);
        foreach (var profile in value.Profiles)
        {
            Guid(stream, 0x30, value.ProviderId); Guid(stream, 0x31, profile.TransportProfileId);
            Bool(stream, 0x32, profile.Active); Int32(stream, 0x33, profile.SortOrder);
            String(stream, 0x34, profile.Label); String(stream, 0x35, profile.Key); String(stream, 0x36, profile.Category);
        }
        return stream.ToArray();
    }

    /// <summary>Returns the immutable domain/version framing used by literal tests.</summary>
    internal static byte[] EncodeFraming()
    {
        using var stream = new MemoryStream();
        stream.Write(Domain); stream.WriteByte(0); stream.WriteByte(1);
        return stream.ToArray();
    }

    private static void Bool(Stream stream, byte tag, bool value) { stream.WriteByte(tag); stream.WriteByte(value ? (byte)1 : (byte)0); }
    private static void Int32(Stream stream, byte tag, int value) { stream.WriteByte(tag); Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); stream.Write(bytes); }
    private static void Int64(Stream stream, byte tag, long value) { stream.WriteByte(tag); Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); stream.Write(bytes); }
    private static void Guid(Stream stream, byte tag, Guid value) { stream.WriteByte(tag); Span<byte> bytes = stackalloc byte[16]; value.TryWriteBytes(bytes, bigEndian: true, out _); stream.Write(bytes); }
    private static void String(Stream stream, byte tag, string value) { stream.WriteByte(tag); var bytes = Encoding.UTF8.GetBytes(value); UInt32(stream, (uint)bytes.Length); stream.Write(bytes); }
    private static void UInt32(Stream stream, uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); stream.Write(bytes); }
    private static void NullableBool(Stream s, byte tag, bool? value) { s.WriteByte(tag); s.WriteByte(value.HasValue ? (byte)1 : (byte)0); if (value.HasValue) s.WriteByte(value.Value ? (byte)1 : (byte)0); }
    private static void NullableInt32(Stream s, byte tag, int? value) { s.WriteByte(tag); s.WriteByte(value.HasValue ? (byte)1 : (byte)0); if (value.HasValue) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, value.Value); s.Write(b); } }
    private static void NullableInt64(Stream s, byte tag, long? value) { s.WriteByte(tag); s.WriteByte(value.HasValue ? (byte)1 : (byte)0); if (value.HasValue) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, value.Value); s.Write(b); } }
    private static void NullableGuid(Stream s, byte tag, Guid? value) { s.WriteByte(tag); s.WriteByte(value.HasValue ? (byte)1 : (byte)0); if (value.HasValue) { Span<byte> b = stackalloc byte[16]; value.Value.TryWriteBytes(b, true, out _); s.Write(b); } }
    private static void NullableString(Stream s, byte tag, string? value) { s.WriteByte(tag); s.WriteByte(value is null ? (byte)0 : (byte)1); if (value is not null) { var b = Encoding.UTF8.GetBytes(value); UInt32(s, (uint)b.Length); s.Write(b); } }
}

/// <summary>Contains exactly the ordered non-secret v1 authority inputs.</summary>
public sealed record MobileRoutingAuthorityProjection(bool FeatureEnabled, long FeatureGeneration, byte AuthorityKind,
    string? PersonalProviderKey, long? SelectionGeneration, Guid? PersonalProfileId, bool? RoutingAuthorized,
    long? RoutingGeneration, long? CredentialGeneration, int? RoutingVerification, long? VerifiedCredentialGeneration,
    long? VerifiedConfigurationGeneration, Guid? SelectedProviderId, long? UserConfigurationVersion,
    bool? CredentialPresent, long? VerifiedUserVersion, long? VerifiedProviderVersion, string? VerificationStatus,
    bool CredentialReadable, Guid ProviderId, bool ProviderEnabled, int Adapter, long ProviderConfigurationVersion,
    long? ProviderVerifiedVersion, int PersonalAccess, IReadOnlyList<MobileRoutingAuthorityProfile> Profiles);

/// <summary>Contains one canonical eligible mapping/profile entry.</summary>
public sealed record MobileRoutingAuthorityProfile(Guid TransportProfileId, bool Active, int SortOrder,
    string Label, string Key, string Category);
