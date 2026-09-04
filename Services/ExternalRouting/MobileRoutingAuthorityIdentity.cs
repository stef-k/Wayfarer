using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Writes the shared canonical binary form used by Mobile routing identities.</summary>
internal sealed class MobileRoutingCanonicalWriter(string domain)
{
    private readonly MemoryStream stream = CreateStream(domain);

    public void Bool(byte tag, bool value) { stream.WriteByte(tag); stream.WriteByte(value ? (byte)1 : (byte)0); }
    public void Int32(byte tag, int value) { stream.WriteByte(tag); Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); stream.Write(bytes); }
    public void Int64(byte tag, long value) { stream.WriteByte(tag); Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); stream.Write(bytes); }
    public void Guid(byte tag, Guid value) { stream.WriteByte(tag); Span<byte> bytes = stackalloc byte[16]; value.TryWriteBytes(bytes, true, out _); stream.Write(bytes); }
    public void String(byte tag, string value) { stream.WriteByte(tag); var bytes = Encoding.UTF8.GetBytes(value); UInt32((uint)bytes.Length); stream.Write(bytes); }
    public void Count(byte tag, int value) { stream.WriteByte(tag); UInt32((uint)value); }
    public void NullableInt32(byte tag, int? value) { stream.WriteByte(tag); stream.WriteByte(value.HasValue ? (byte)1 : (byte)0); if (value.HasValue) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value.Value); stream.Write(bytes); } }
    public byte[] ToArray() => stream.ToArray();

    private void UInt32(uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); stream.Write(bytes); }
    private static MemoryStream CreateStream(string domain)
    {
        var result = new MemoryStream();
        result.Write(Encoding.ASCII.GetBytes(domain)); result.WriteByte(0); result.WriteByte(1);
        return result;
    }
}

/// <summary>Owns shared hashing and strict canonical public syntax.</summary>
internal static class MobileRoutingCanonicalIdentity
{
    public static string Compute(byte[] bytes) => "v1." + Base64Url(SHA256.HashData(bytes));
    public static bool IsValid(string? value)
    {
        if (value is not { Length: 46 } || value.Length > 64 || !value.StartsWith("v1.", StringComparison.Ordinal)
            || value.AsSpan(3).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") >= 0)
            return false;
        Span<byte> decoded = stackalloc byte[32];
        return Convert.TryFromBase64String(value[3..].Replace('-', '+').Replace('_', '/') + "=", decoded, out var written)
            && written == decoded.Length && string.Equals(value[3..], Base64Url(decoded), StringComparison.Ordinal);
    }
    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Computes the opaque identity of ordered discovery choices.</summary>
public static class DiscoveryCatalogIdentity
{
    private const string Domain = "Wayfarer.MobileRoutingDiscoveryCatalog";
    public static bool IsValid(string? value) => MobileRoutingCanonicalIdentity.IsValid(value);
    public static string Compute(MobileRoutingDiscoveryCatalogProjection value) => MobileRoutingCanonicalIdentity.Compute(Encode(value));
    internal static byte[] EncodeFraming() => new MobileRoutingCanonicalWriter(Domain).ToArray();
    internal static byte[] Encode(MobileRoutingDiscoveryCatalogProjection value)
    {
        var writer = new MobileRoutingCanonicalWriter(Domain);
        writer.String(0x10, value.Outcome); writer.Count(0x11, value.Profiles.Count);
        foreach (var profile in value.Profiles)
        {
            writer.Guid(0x20, profile.TransportProfileId); writer.String(0x21, profile.DisplayName);
            writer.String(0x22, profile.ModeKey); writer.String(0x23, profile.Category);
        }
        writer.Count(0x12, value.Modes.Count);
        foreach (var mode in value.Modes)
        { writer.String(0x24, mode.Key); writer.String(0x25, mode.Label); }
        return writer.ToArray();
    }
}

/// <summary>Computes the opaque identity of one selected executable authority.</summary>
public static class SelectedProfileAuthorityIdentity
{
    private const string Domain = "Wayfarer.MobileRoutingSelectedProfileAuthority";
    public static bool IsValid(string? value) => MobileRoutingCanonicalIdentity.IsValid(value);
    public static string Compute(MobileRoutingSelectedProfileAuthorityProjection value) => MobileRoutingCanonicalIdentity.Compute(Encode(value));
    internal static byte[] EncodeFraming() => new MobileRoutingCanonicalWriter(Domain).ToArray();
    internal static byte[] Encode(MobileRoutingSelectedProfileAuthorityProjection value)
    {
        var writer = new MobileRoutingCanonicalWriter(Domain);
        writer.String(0x10, value.UserId); writer.String(0x11, value.ProviderKey);
        writer.Guid(0x12, value.TransportProfileId); writer.String(0x13, value.NativeMode);
        writer.Int32(0x14, value.CatalogVersion); writer.Int32(0x15, value.SelectionGeneration);
        writer.Int32(0x16, value.CredentialGeneration); writer.Int32(0x17, value.RoutingGeneration);
        writer.Bool(0x18, value.RoutingAuthorized); writer.Int32(0x19, value.RoutingVerification);
        writer.NullableInt32(0x1a, value.VerifiedCredentialGeneration);
        writer.NullableInt32(0x1b, value.VerifiedRoutingGeneration);
        return writer.ToArray();
    }
}

public sealed record MobileRoutingDiscoveryCatalogProjection(string Outcome, IReadOnlyList<MobileRoutingProfile> Profiles,
    IReadOnlyList<ProviderDirectionsMode>? ProviderModes = null)
{
    public IReadOnlyList<ProviderDirectionsMode> Modes => ProviderModes ?? [];
}
public sealed record MobileRoutingSelectedProfileAuthorityProjection(
    string UserId, string ProviderKey, Guid TransportProfileId, string NativeMode, int CatalogVersion,
    int SelectionGeneration, int CredentialGeneration, int RoutingGeneration, bool RoutingAuthorized,
    int RoutingVerification, int? VerifiedCredentialGeneration, int? VerifiedRoutingGeneration);
