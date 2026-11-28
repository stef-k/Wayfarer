using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Planning;
using Wayfarer.Util;
using static Wayfarer.Services.TripMetadataReceipts.TripMetadataIdentity;

namespace Wayfarer.Services.TripMetadataReceipts;

/// <summary>Encodes published comparison values and private deletion markers for typed receipts.</summary>
internal static class TripStructureDigests
{
    private const string ResultContract = "trip-region-place-area-result-1";

    /// <summary>Field digests for the selected or defaulted Region scalars.</summary>
    internal static string Region(Region region, string field) => field switch
    {
        "center" => Position("region", field, region.Center),
        "name" => Text("region", field, region.Name),
        "notes" => Text("region", field, RichNotes.Normalize(region.Notes)),
        "coverImage" => Text("region", field, region.CoverImageUrl),
        _ => throw new InvalidOperationException("Unexpected Region digest field.")
    };

    /// <summary>Field digests for the selected or defaulted Place scalars.</summary>
    internal static string Place(Place place, string field) => field == "location"
        ? Position("place", field, place.Location)
        : Text("place", field, (string?)PlaceConditionalMutationService.Current(place, field));

    /// <summary>Field digests for the selected or defaulted Area scalars.</summary>
    internal static string Area(Area area, string field) => Text("area", field, field switch
    {
        "name" => area.Name, "notes" => RichNotes.Normalize(area.Notes), "fillHex" => area.FillHex,
        "geometry" => AreaGeometryIdentity.For(area.Geometry),
        _ => throw new InvalidOperationException("Unexpected Area digest field.")
    });

    /// <summary>Hashes the complete saved tuple with microsecond UTC timestamp precision.</summary>
    internal static object AddressEffect(Place place, string geocode, string tupleState) => new
    {
        geocode, tupleState,
        tupleDigests = new
        {
            resolvedFeatureName = Text("place", "resolvedFeatureName", place.ResolvedFeatureName),
            resolvedFeatureType = Text("place", "resolvedFeatureType", place.ResolvedFeatureType),
            addressEnrichmentProvider = Text("place", "addressEnrichmentProvider", place.AddressEnrichmentProvider),
            addressEnrichmentStorageMode = Text("place", "addressEnrichmentStorageMode", place.AddressEnrichmentStorageMode),
            addressEnrichedAt = Text("place", "addressEnrichedAt",
                place.AddressEnrichedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture))
        },
        warningCodes = geocode == "fallback" ? new[] { "reverse-geocode-unavailable" } : Array.Empty<string>()
    };

    /// <summary>Summarizes removed private Planning without exposing its contents.</summary>
    internal static string? Planning(PlanningAggregate? planning) => planning == null ? null
        : "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Utf8(PlanningCodec.Write(planning))));

    /// <summary>Frames the exact text value, including a distinct marker for null.</summary>
    internal static string Text(string entity, string field, string? value) =>
        Hash(Utf8(ResultContract), Utf8(entity), Utf8(field), Utf8(value == null ? "n" : "s"), Utf8(value ?? ""));

    /// <summary>Frames finite published coordinates in latitude/longitude order.</summary>
    private static string Position(string entity, string field, Point? point) =>
        point is { IsEmpty: false } && double.IsFinite(point.X) && double.IsFinite(point.Y)
            ? Hash(Utf8(ResultContract), Utf8(entity), Utf8(field), Utf8("p"), Float(point.Y), Float(point.X))
            : Text(entity, field, null);

    /// <summary>Preserves finite binary64 values while normalizing signed zero.</summary>
    private static byte[] Float(double value)
    {
        var bytes = new byte[sizeof(double)];
        BinaryPrimitives.WriteDoubleBigEndian(bytes, value == 0 ? 0.0 : value);
        return bytes;
    }
}
