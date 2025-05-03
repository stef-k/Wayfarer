using NetTopologySuite.Geometries;
using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models;

public class Geofence
{
    public int Id { get; set; }
    public required string UserId { get; set; }

    // Center of the geofence
    [Required]
    public required Point Center { get; set; }

    // Radius in meters
    public double Radius { get; set; }

    // Optional: A name for the geofence
    public string? Name { get; set; }

    // Additional fields as needed
    public string? Notes { get; set; }

}