namespace Wayfarer.Models.Dtos;

public class UserLocationStatsDto
{
    public int TotalLocations { get; set; }
    public int CountriesVisited { get; set; }
    public int CitiesVisited { get; set; }
    public int RegionsVisited { get; set; }
    
    public DateTime? FromDate    { get; set; }
    public DateTime? ToDate      { get; set; }
}