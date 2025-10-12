using NetTopologySuite.Geometries;

namespace Wayfarer.Models.Dtos;

/// <summary>
/// Represents detailed information about a country visit
/// </summary>
public class CountryVisitDetail
{
    /// <summary>
    /// Name of the country
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// First recorded visit to this country
    /// </summary>
    public DateTime FirstVisit { get; set; }

    /// <summary>
    /// Most recent visit to this country
    /// </summary>
    public DateTime LastVisit { get; set; }

    /// <summary>
    /// Total number of location records in this country
    /// </summary>
    public int VisitCount { get; set; }

    /// <summary>
    /// Indicates if this is likely the user's home country (high frequency, long duration)
    /// </summary>
    public bool IsHomeCountry { get; set; }

    /// <summary>
    /// Representative coordinates for this country (centroid/average of all user's locations there)
    /// PostGIS Point type storing longitude and latitude
    /// </summary>
    public Point? Coordinates { get; set; }
}
