namespace Wayfarer.Models.Planning;

/// <summary>One partial assessment; independent results are never overwritten by unrelated Unknowns.</summary>
public sealed record PlanningComparison(string Status, string Message, double? Minutes = null);

/// <summary>Bounded pure comparisons over complete supported placements, without storage or providers.</summary>
public static class PlanningComparisons
{
    /// <summary>Compares full child coverage with each admissible selected-window union, preserving gaps.</summary>
    public static PlanningComparison Containment(PlanningPlacements child, IReadOnlyList<PlanningPlacements> windows)
    {
        var error = Problem(new[] { child }.Concat(windows));
        if (error != null) return error;
        if (windows.Count == 0) return new("unknown", "Choose explicitly relevant parent windows; ownership alone imposes no constraint.");
        List<List<PlanningInterval>> unions = [[]];
        foreach (var window in windows)
        {
            if ((long)unions.Count * window.Values.Count * child.Values.Count > 256)
                return new("unknown", "More than 256 admissible placements; refine the selected windows or folds.");
            unions = unions.SelectMany(u => window.Values.Select(v => u.Append(v).ToList())).ToList();
        }
        if (unions.Any(u => u.Any(w => w.Frame != child.Values[0].Frame)))
            return new("unknown", "Child and selected windows need the same explicit instant or interpreted civil frame.");
        var outside = child.Values.SelectMany(c => unions.Select(u => Outside(c, u))).ToArray();
        var violations = outside.Count(v => v.Outside);
        if (violations == 0) return new("completed", "Complete child coverage is inside the selected parent-window union.");
        if (violations < outside.Length) return new("possible", "Some supported placements exceed the selected union; others are contained. Resolve the fold for certainty.");
        var exact = outside.Length == 1 && child.Values[0].Frame == "instant";
        var minutes = exact ? outside[0].Ticks / (double)PlanningIntervals.TicksPerMinute : (double?)null;
        return new("mismatch", exact && !child.Values[0].Moment
            ? $"{minutes:0.###} minutes definitely outside the selected parent-window union."
            : "Every admissible placement has coverage outside the selected union.", minutes);
    }

    /// <summary>Physical conclusions require assessed presence and explicit incompatible-location/single-traveller premises.</summary>
    public static PlanningComparison Physical(PlanningPlacements first, PlanningPlacements second, bool singleTraveller, bool incompatible)
    {
        var error = Problem([first, second]);
        if (error != null) return error;
        if (first.Values[0].Frame != second.Values[0].Frame) return new("unknown", "Occupation needs a common comparable frame.");
        var overlap = first.Values.SelectMany(a => second.Values.Select(b => !a.Moment && !b.Moment && a.Start < b.End && b.Start < a.End)).ToArray();
        if (!overlap.Any(v => v)) return new("completed", "No occupied overlap in these placements. Adjacency establishes no transfer time.");
        if (!singleTraveller || !incompatible) return new("unknown", "Occupied overlap needs an explicit single-traveller premise and assessed incompatible locations.");
        return overlap.All(v => v) ? new("contradiction", "One traveller requires simultaneous presence at assessed incompatible locations.")
            : new("possible", "Some supported placements require simultaneous presence at incompatible locations.");
    }

    /// <summary>Reports known ordering/risk before unresolved extras; estimates never become guarantees.</summary>
    public static IReadOnlyList<PlanningComparison> Movement(PlanningPlacements release, PlanningPlacements arrival,
        double? estimate, double? additional, bool extrasKnown)
    {
        var error = Problem([release, arrival]);
        if (error != null) return [error];
        if (release.Values[0].Frame != "instant" || arrival.Values[0].Frame != "instant")
            return [new("unknown", "Elapsed travel needs resolved zones.")];
        var gaps = release.Values.SelectMany(r => arrival.Values.Select(a => (a.Start - r.End) / (double)PlanningIntervals.TicksPerMinute)).Distinct().ToArray();
        if (gaps.All(g => g < 0)) return [new("contradiction", gaps.Length == 1 ? $"Negative exact gap: {-gaps[0]:0.###} minutes in the wrong order." : "Every supported gap is negative.", gaps.Length == 1 ? -gaps[0] : null)];
        if (gaps.Length != 1) return [new("unknown", "Travel gap depends on an unresolved fold; choose the occurrence.")];
        if (estimate == null) return [new("unknown", $"{gaps[0]:0.###}-minute gap; a relevant travel estimate is unknown, not zero.")];
        var baseline = gaps[0] - estimate.Value;
        List<PlanningComparison> results = [];
        if (baseline < 0) results.Add(new("risk", $"{-baseline:0.###}-minute estimate risk before extras.", -baseline));
        if (!extrasKnown || additional == null) results.Add(new("unknown", "Allowance or necessary connection information remains unknown; known estimate risk is retained."));
        else
        {
            var slack = baseline - additional.Value;
            if (slack >= 0) results.Add(new("completed", $"{slack:0.###} minutes estimated slack ({gaps[0]:0.###} gap − {estimate:0.###} estimate − {additional:0.###} additional). No arrival guarantee.", slack));
            else if (additional > 0) results.Add(new("risk", $"{-slack:0.###}-minute estimate risk including additional allowance/connection.", -slack));
        }
        return results;
    }

    /// <summary>Any inconsistent selected input invalidates that comparison, never the independent checks.</summary>
    private static PlanningComparison? Problem(IEnumerable<PlanningPlacements> values)
    {
        var list = values.ToArray();
        var inconsistent = list.FirstOrDefault(v => v.Inconsistent);
        if (inconsistent != null) return new("inconsistent", inconsistent.Problem!);
        var missing = list.FirstOrDefault(v => v.Problem != null || v.Values.Count == 0);
        return missing == null ? null : new("unknown", missing.Problem ?? "No supported placements.");
    }

    /// <summary>Sorted union consumes each covered interval once, retaining every internal gap and exclusive end.</summary>
    private static (bool Outside, long Ticks) Outside(PlanningInterval child, IEnumerable<PlanningInterval> windows)
    {
        var sorted = windows.OrderBy(w => w.Start).ToArray();
        if (child.Moment) return (!sorted.Any(w => child.Start >= w.Start && child.Start < w.End), 0);
        long cursor = child.Start, outside = 0;
        foreach (var window in sorted)
        {
            if (window.End <= cursor || window.Start >= child.End) continue;
            if (window.Start > cursor) outside += window.Start - cursor;
            cursor = Math.Max(cursor, Math.Min(child.End, window.End));
        }
        outside += child.End - cursor;
        return (outside > 0, outside);
    }
}
