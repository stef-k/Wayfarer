namespace Wayfarer.Models.ViewModels;

public class TripPrintViewModel
{
    public Trip Trip { get; init; } = null!;
    public List<Region> Regions { get; init; } = new();
    public List<Place> Places { get; init; } = new();
    public List<Segment> Segments { get; init; } = new();
    public IDictionary<string, string> Snap { get; init; } = new Dictionary<string, string>();
}
