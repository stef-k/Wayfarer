using NetTopologySuite.Geometries;

namespace Wayfarer.Models.Dtos;

/// <summary>
/// Represents detailed information about a region visit
/// </summary>
public class RegionVisitDetail
{
    /// <summary>
    /// Name of the region
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Parent country name
    /// </summary>
    public string CountryName { get; set; } = string.Empty;

    /// <summary>
    /// First recorded visit to this region
    /// </summary>
    public DateTime FirstVisit { get; set; }

    /// <summary>
    /// Most recent visit to this region
    /// </summary>
    public DateTime LastVisit { get; set; }

    /// <summary>
    /// Total number of location records in this region
    /// </summary>
    public int VisitCount { get; set; }

    /// <summary>
    /// Representative coordinates for this region (centroid/average of all user's locations there)
    /// PostGIS Point type storing longitude and latitude
    /// </summary>
    public Point? Coordinates { get; set; }
}
