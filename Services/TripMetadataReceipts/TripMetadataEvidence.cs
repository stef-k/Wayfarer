using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wayfarer.Services.TripMetadataReceipts;

/// <summary>Correlation shared by the authenticated permit and every evidence response.</summary>
internal sealed record TripMetadataIdentity(string Contract, string SubjectId, string Adapter, string Kind,
    Guid TripId, string RequestDigest, Guid OperationId, DateTime ExecuteBeforeUtc)
{
    internal const string Version = "trip-metadata-receipt-1";
    internal const string OperationKind = "metadata";
    internal const string ProofHeader = "X-Wayfarer-Trip-Attempt";

    /// <summary>Requires the published lowercase SHA-256 representation.</summary>
    internal static bool IsDigest(string? value) => value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal)
        && !value.AsSpan(7).ContainsAnyExcept("0123456789abcdef");

    /// <summary>Frames the actual transport bytes without JSON parsing or normalization.</summary>
    internal static string Digest(string subject, string adapter, Guid tripId, byte[] body) =>
        Hash(Utf8(Version), Utf8(OperationKind), Utf8(adapter), Utf8(subject), Utf8(tripId.ToString("D")), body);

    /// <summary>Hashes one exact nullable published comparison value.</summary>
    internal static string FieldDigest(string field, string? value) =>
        Hash(Utf8("trip-metadata-result-1"), Utf8(field), Utf8(value == null ? "n" : "s"), Utf8(value ?? ""));

    /// <summary>Encodes one text frame without a BOM.</summary>
    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    /// <summary>Hashes four-byte unsigned big-endian lengths followed by the frame bytes.</summary>
    private static string Hash(params byte[][] frames)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var frame in frames)
        {
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)frame.Length);
            hash.AppendData(length);
            hash.AppendData(frame);
        }
        return "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Writes a flat envelope with the same publicly named correlation fields for all routes.</summary>
    internal Dictionary<string, object?> Envelope() => new()
    {
        ["contract"] = Contract, ["subjectId"] = SubjectId, ["adapter"] = Adapter, ["kind"] = Kind,
        ["tripId"] = TripId, ["requestDigest"] = RequestDigest, ["operationId"] = OperationId, ["executeBeforeUtc"] = ExecuteBeforeUtc
    };
}

/// <summary>Stored terminal result, or the outcome of one synchronized observation.</summary>
internal sealed record TripMetadataEvidence(string State, string HistoricalOutcome, bool? FutureEffects)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CompletedAtUtc { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ReceiptRetainUntilUtc { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? UpdatedAtUtc { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? FieldDigests { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ObservedAtUtc { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; init; }

    /// <summary>Combines mandatory correlation and bounded evidence without exposing a private Trip.</summary>
    internal Dictionary<string, object?> Envelope(TripMetadataIdentity identity)
    {
        var envelope = identity.Envelope();
        foreach (var property in JsonSerializer.SerializeToElement(this, JsonSerializerOptions.Web).EnumerateObject())
            envelope.Add(property.Name, property.Value);
        return envelope;
    }
}
