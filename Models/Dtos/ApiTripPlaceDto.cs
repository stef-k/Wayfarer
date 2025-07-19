namespace Wayfarer.Models.Dtos;

public class ApiTripPlaceDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Notes { get; set; }
    public int? DisplayOrder { get; set; }
    public string? IconName { get; set; }
    public string? MarkerColor { get; set; }
    public string? Address { get; set; }
    public double[]? Location { get; set; }  // [lon, lat]
}