namespace Wayfarer.Models.Dtos;

/// <summary>
/// Detailed statistics about user location data including lists of countries, regions, and cities
/// </summary>
public class UserLocationStatsDetailedDto
{
    /// <summary>
    /// Total number of location records
    /// </summary>
    public int TotalLocations { get; set; }

    /// <summary>
    /// Detailed list of countries visited with visit information
    /// </summary>
    public List<CountryVisitDetail> Countries { get; set; } = new();

    /// <summary>
    /// List of unique region names visited
    /// </summary>
    public List<string> Regions { get; set; } = new();

    /// <summary>
    /// List of unique city names visited
    /// </summary>
    public List<string> Cities { get; set; } = new();

    /// <summary>
    /// Earliest location date
    /// </summary>
    public DateTime? FromDate { get; set; }

    /// <summary>
    /// Latest location date
    /// </summary>
    public DateTime? ToDate { get; set; }
}
