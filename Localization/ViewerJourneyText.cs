using Wayfarer.Models.ViewModels;

namespace Wayfarer.Localization;

/// <summary>Formats live Segment wording while the resolver and standalone print keep their existing contract.</summary>
public static class ViewerJourneyText
{
    /// <summary>Localizes the role surrounding an authored Place name.</summary>
    public static string Role(ViewerJourneyAnchor anchor, WebMessages text) => anchor.Role switch
    {
        "Start" => text["Viewer.Start"],
        "End" => text["Viewer.End"],
        _ => text["Viewer.Via", text.DisplayNumber(anchor.Position)]
    };

    /// <summary>Translates generated placeholders while retaining authored names.</summary>
    public static string Name(ViewerJourneyAnchor? anchor, WebMessages text) => anchor switch
    {
        null => text["Viewer.PlaceUnavailable"],
        { IsPlaceholder: false } => anchor.DisplayName,
        { Role: "Start" } => text["Viewer.UnlinkedStart"],
        { Role: "End" } => text["Viewer.UnlinkedEnd"],
        _ => text["Viewer.UnnamedWaypoint"]
    };

    /// <summary>Formats the ordered trail with optional map labels.</summary>
    public static string? Trail(ViewerSegmentJourney journey, WebMessages text, bool labels) => journey.Anchors.Count < 2
        ? null : string.Join(" → ", journey.Anchors.Select(anchor => labels ? $"{anchor.Label} {Name(anchor, text)}" : Name(anchor, text)));

    /// <summary>Formats the ordered journey name for assistive technology.</summary>
    public static string? AccessibleName(ViewerSegmentJourney journey, WebMessages text)
    {
        var anchors = journey.Anchors;
        if (anchors.Count < 2) return null;
        var first = Name(anchors[0], text);
        var last = Name(anchors[^1], text);
        return anchors.Count == 2 ? text["Viewer.SegmentFromTo", first, last]
            : text["Viewer.SegmentVia", first, string.Join(text["Viewer.Then"], anchors.Skip(1).SkipLast(1).Select(anchor => Name(anchor, text))), last];
    }

    /// <summary>Maps resolver diagnostics at the presentation boundary.</summary>
    public static string Degradation(string diagnostic, WebMessages text) => diagnostic switch
    {
        "Journey order is unavailable." => text["Viewer.JourneyOrderUnavailable"],
        "Route direction unavailable" => text["Viewer.RouteDirectionUnavailable"],
        "Route line is unavailable." => text["Viewer.RouteLineUnavailable"],
        _ => text["Viewer.JourneyDetailsUnavailable"]
    };

    /// <summary>Displays the existing coarse duration with the live locale's units.</summary>
    public static string? Duration(TimeSpan? duration, WebMessages text) => duration is { } value
        ? value.TotalMinutes < 60 ? text["Viewer.MinutesUnit", (int)value.TotalMinutes]
        : text["Viewer.HoursUnit", (int)value.TotalHours] : null;
}
