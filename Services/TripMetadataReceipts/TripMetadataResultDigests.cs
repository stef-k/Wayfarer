using System.Buffers.Binary;
using Wayfarer.Models;
using Wayfarer.Util;
using static Wayfarer.Services.TripMetadataReceipts.TripMetadataIdentity;

namespace Wayfarer.Services.TripMetadataReceipts;

/// <summary>Fingerprints canonical persisted values using the published field framing.</summary>
internal static class TripMetadataResultDigests
{
    /// <summary>Name and Notes retain their original digest across both protocols.</summary>
    internal static string For(string field, Trip trip)
    {
        if (field is "name" or "notes")
            return FieldDigest(field, field == "name" ? trip.Name : RichNotes.Normalize(trip.Notes));
        byte[][] payload = field switch
        {
            "coverImage" => Text(trip.CoverImageUrl),
            "contributionPolicy" => Text(trip.ContributionPolicy),
            "isPublic" => [Utf8("b"), Utf8(trip.IsPublic ? "1" : "0")],
            "zoom" => trip.Zoom is int zoom ? [Utf8("i"), Int32(zoom)] : [Utf8("n"), []],
            "center" => [Utf8("p"), .. Coordinate(trip.CenterLat), .. Coordinate(trip.CenterLon)],
            "credits" => [Utf8("c"), Int32(trip.Credits.Count), .. trip.Credits.SelectMany(Credit)],
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        return Hash([Utf8("trip-metadata-result-2"), Utf8(field), .. payload]);
    }

    /// <summary>Encodes an ordered canonical row including each nullable member.</summary>
    private static byte[][] Credit(TripCredit credit) =>
        [.. Text(credit.Contributor), .. Text(credit.SourceTitle), .. Text(credit.Licence),
            .. Text(credit.LicenceUrl), .. Text(credit.Notices), .. Text(credit.Modifications), .. Text(credit.SourceUrl)];

    /// <summary>Separates null text from empty text.</summary>
    private static byte[][] Text(string? text) => text == null ? [Utf8("n"), []] : [Utf8("s"), Utf8(text)];

    /// <summary>Uses the same representation for both signs of zero.</summary>
    private static byte[][] Coordinate(double? value)
    {
        if (value is not double number) return [Utf8("n"), []];
        var bytes = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(bytes, number == 0 ? 0 : number);
        return [Utf8("d"), bytes];
    }

    /// <summary>Encodes a stored integer or nonnegative row count.</summary>
    private static byte[] Int32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }
}
