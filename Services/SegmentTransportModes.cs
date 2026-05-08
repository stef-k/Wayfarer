using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

/// <summary>
/// Provides the legacy segment transport modes and speeds used for duration estimation.
/// </summary>
public static class SegmentTransportModes
{
    /// <summary>Exact legacy transport mode options in the order shown by the segment form.</summary>
    public static readonly IReadOnlyList<EditorTransportModeDto> Options =
    [
        new("walk", "Walk", 5),
        new("bicycle", "Bicycle", 15),
        new("bike", "Bike (Motorbike)", 40),
        new("car", "Car", 60),
        new("bus", "Bus", 35),
        new("train", "Train", 100),
        new("ferry", "Ferry", 30),
        new("boat", "Boat", 25),
        new("flight", "Flight", 800),
        new("helicopter", "Helicopter", 200)
    ];

    /// <summary>Speed lookup keyed by legacy segment mode value.</summary>
    public static readonly IReadOnlyDictionary<string, double> SpeedsKmh =
        Options.ToDictionary(mode => mode.Value, mode => mode.SpeedKmh, StringComparer.OrdinalIgnoreCase);
}
