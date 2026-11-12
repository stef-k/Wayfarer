using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Foreign references without transferable media permission are omitted, with explicit caller feedback.</summary>
internal static class TripCopyMedia
{
    /// <summary>Does not equate a sanitized/proxied URL with permission to copy or rehost it.</summary>
    internal static bool OmitUnqualified(Trip trip)
    {
        var omitted = trip.CoverImageUrl != null;
        trip.CoverImageUrl = null;
        foreach (var region in trip.Regions)
        {
            omitted |= region.CoverImageUrl != null;
            region.CoverImageUrl = null;
        }
        foreach (var target in NativeTripGraph.Targets(trip))
        {
            var notes = target.Target switch
            {
                Trip t => t.Notes, Region t => t.Notes, Place t => t.Notes,
                Area t => t.Notes, Segment t => t.Notes, _ => null
            };
            if (notes == null) continue;
            var document = new HtmlParser().ParseDocument(notes);
            var images = document.QuerySelectorAll("img").ToArray();
            if (images.Length == 0) continue;
            omitted = true;
            foreach (var image in images) image.Remove();
            var safe = document.Body!.InnerHtml;
            switch (target.Target)
            {
                case Trip t: t.Notes = safe; break;
                case Region t: t.Notes = safe; break;
                case Place t: t.Notes = safe; break;
                case Area t: t.Notes = safe; break;
                case Segment t: t.Notes = safe; break;
            }
        }
        return omitted;
    }
}
