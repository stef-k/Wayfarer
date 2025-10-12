using NetTopologySuite.Geometries;

namespace Wayfarer.Models.Dtos;

/// <summary>
/// Represents detailed information about a city visit
/// </summary>
public class CityVisitDetail
{
    /// <summary>
    /// Name of the city
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Parent region name
    /// </summary>
    public string RegionName { get; set; } = string.Empty;

    /// <summary>
    /// Parent country name
    /// </summary>
    public string CountryName { get; set; } = string.Empty;

    /// <summary>
    /// First recorded visit to this city
    /// </summary>
    public DateTime FirstVisit { get; set; }

    /// <summary>
    /// Most recent visit to this city
    /// </summary>
    public DateTime LastVisit { get; set; }

    /// <summary>
    /// Total number of location records in this city
    /// </summary>
    public int VisitCount { get; set; }

    /// <summary>
    /// Representative coordinates for this city (centroid/average of all user's locations there)
    /// PostGIS Point type storing longitude and latitude
    /// </summary>
    public Point? Coordinates { get; set; }
}
