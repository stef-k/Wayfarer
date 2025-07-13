namespace Wayfarer.Models.ViewModels;

public class TripPrintViewModel
{
    public Trip                         Trip     { get; init; }
    public List<Region>                 Regions  { get; init; }
    public List<Place>                  Places   { get; init; }
    public List<Segment>                Segments { get; init; }
    public IDictionary<string,string>   Snap     { get; init; } 
}