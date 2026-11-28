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
    internal const string Version2 = "trip-metadata-receipt-2";
    internal const string StructureVersion = Models.Dtos.TripStructureOwnerSnapshot.Contract;
    internal const string OperationKind = "metadata";
    internal const string ProofHeader = "X-Wayfarer-Trip-Attempt";

    /// <summary>Typed path binding for protected Region, Place and Area operations.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TripReceiptTarget? Target { get; init; }

    /// <summary>Requires the published lowercase SHA-256 representation.</summary>
    internal static bool IsDigest(string? value) => value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal)
        && !value.AsSpan(7).ContainsAnyExcept("0123456789abcdef");

    /// <summary>Frames the actual transport bytes without JSON parsing or normalization.</summary>
    internal static string Digest(string subject, string adapter, Guid tripId, byte[] body) =>
        Digest(Version, OperationKind, subject, adapter, tripId, body);

    /// <summary>Frames version and route identity with the exact body bytes.</summary>
    internal static string Digest(string contract, string kind, string subject, string adapter, Guid tripId, byte[] body) =>
        Hash(Utf8(contract), Utf8(kind), Utf8(adapter), Utf8(subject), Utf8(tripId.ToString("D")), body);

    /// <summary>Hashes one exact nullable published comparison value.</summary>
    internal static string FieldDigest(string field, string? value) =>
        Hash(Utf8("trip-metadata-result-1"), Utf8(field), Utf8(value == null ? "n" : "s"), Utf8(value ?? ""));

    /// <summary>Encodes one text frame without a BOM.</summary>
    internal static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    /// <summary>Hashes four-byte unsigned big-endian lengths followed by the frame bytes.</summary>
    internal static string Hash(params byte[][] frames)
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
    internal Dictionary<string, object?> Envelope()
    {
        var envelope = new Dictionary<string, object?>
        {
            ["contract"] = Contract, ["subjectId"] = SubjectId, ["adapter"] = Adapter, ["kind"] = Kind, ["tripId"] = TripId
        };
        if (Target != null) envelope.Add("target", Target);
        envelope.Add("requestDigest", RequestDigest);
        envelope.Add("operationId", OperationId);
        envelope.Add("executeBeforeUtc", ExecuteBeforeUtc);
        return envelope;
    }

    /// <summary>Frames the typed route and the exact submitted bytes.</summary>
    internal string StructureDigest(byte[] body)
    {
        var target = Target ?? throw new InvalidOperationException("A typed digest requires a route target.");
        return Hash(Utf8(Contract), Utf8(Kind), Utf8(Adapter), Utf8(SubjectId), Utf8(TripId.ToString("D")),
            Utf8(target.Type), Utf8(target.Id.ToString("D")), body);
    }
}

/// <summary>Exact path identity of the protected write target.</summary>
internal sealed record TripReceiptTarget(string Type, Guid Id);

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

    /// <summary>Versioned commit projection for a typed Trip operation.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; init; }

    /// <summary>Field guidance on a v2 validation refusal, preserved for replay and lookup.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string[]>? Errors { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TripPrivacyEffect? PrivacyEffect { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TripTagEffect? TagResult { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TripSharingEffect? SharingResult { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TripSavedView? SavedView { get; init; }

    /// <summary>Combines mandatory correlation and bounded evidence without exposing a private Trip.</summary>
    internal Dictionary<string, object?> Envelope(TripMetadataIdentity identity)
    {
        var envelope = identity.Envelope();
        foreach (var property in JsonSerializer.SerializeToElement(this, JsonSerializerOptions.Web).EnumerateObject())
            envelope.Add(property.Name, property.Value);
        return envelope;
    }
}

/// <summary>Stored projection of a privacy selection and its derived effect.</summary>
internal sealed record TripPrivacyEffect(bool ShareProgressEnabled, bool DisabledShareProgress);

/// <summary>Stored publication and sharing at the operation's commit.</summary>
internal sealed record TripSharingEffect(bool IsPublic, bool ShareProgressEnabled);

/// <summary>Historical saved view including independently nullable coordinates.</summary>
internal sealed record TripSavedView(Models.Dtos.TripMetadataCenter Center, int? Zoom);

/// <summary>Exact catalogue identity of a tag or join.</summary>
internal sealed record TripTagIdentity(Guid Id, string Slug);

/// <summary>Actual join and catalogue effects, with resulting membership at commit.</summary>
internal sealed record TripTagEffect(TripTagIdentity[] AddedJoins, TripTagIdentity[] RemovedJoins,
    TripTagIdentity[] CreatedCatalogue, TripTagIdentity[] DeletedCatalogue, Models.Dtos.TripTagDto[] ResultingTags)
{
    /// <summary>Whether the operation changed a join or catalogue row.</summary>
    internal bool Applied => AddedJoins.Length + RemovedJoins.Length + CreatedCatalogue.Length + DeletedCatalogue.Length != 0;
}
