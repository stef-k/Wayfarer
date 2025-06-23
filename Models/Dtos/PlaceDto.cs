using NetTopologySuite.Geometries;

namespace Wayfarer.Models.Dtos;

public class PlaceDto
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public Point Location { get; set; }
}