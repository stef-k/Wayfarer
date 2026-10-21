using System.Security.Cryptography;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Wayfarer.Util;

/// <summary>Exact persisted Polygon evidence, independent of rounded display GeoJSON or topological equality.</summary>
internal static class AreaGeometryIdentity
{
    /// <summary>
    /// Hashes fixed little-endian EWKB including SRID, every XY double, ring order and holes.
    /// No normalization, rounding, ring rotation or orientation change is permitted.
    /// This comparison value is neither authorization nor a durable mutation receipt.
    /// </summary>
    internal static string For(Polygon polygon) =>
        "area-polygon-1:" + Convert.ToHexString(SHA256.HashData(new WKBWriter(ByteOrder.LittleEndian, true).Write(polygon)));
}
