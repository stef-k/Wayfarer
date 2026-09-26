using Wayfarer.Models;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>Normalizes a newly supplied or copied aggregate before it becomes durable; never used for reads.</summary>
internal static class TripRichNotes
{
    /// <summary>Applies the shared policy to all five rich-note domains in the destination aggregate.</summary>
    internal static void NormalizeDestination(Trip trip)
    {
        trip.Notes = RichNotes.NormalizeForPersistence(trip.Notes);
        foreach (var region in trip.Regions ?? [])
        {
            region.Notes = RichNotes.NormalizeForPersistence(region.Notes);
            foreach (var place in region.Places ?? []) place.Notes = RichNotes.NormalizeForPersistence(place.Notes);
            foreach (var area in region.Areas ?? []) area.Notes = RichNotes.NormalizeForPersistence(area.Notes);
        }
        foreach (var segment in trip.Segments ?? []) segment.Notes = RichNotes.NormalizeForPersistence(segment.Notes);
    }
}
