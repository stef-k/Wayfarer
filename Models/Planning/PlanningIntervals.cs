using NodaTime;
using NodaTime.Text;

namespace Wayfarer.Models.Planning;

/// <summary>Transient coverage in one explicit frame; authored planning is never rewritten.</summary>
public sealed record PlanningInterval(long Start, long End, string Frame)
{
    /// <summary>Zero allocation is an explicitly assessed moment, not unknown dwell.</summary>
    public bool Moment => Start == End;
}

/// <summary>All supported placements, or a named limit rather than a fabricated interval.</summary>
public sealed record PlanningPlacements(IReadOnlyList<PlanningInterval> Values, string? Problem = null, bool Inconsistent = false);

/// <summary>Serving-TZDB value seam shared by context, occupation and movement comparisons.</summary>
public static class PlanningIntervals
{
    private static readonly LocalDateTimePattern Minute = LocalDateTimePattern.CreateWithInvariantCulture("uuuu-MM-dd'T'HH:mm");
    /// <summary>Ticks preserve exact authored minutes and offset seconds without floating point time arithmetic.</summary>
    public const long TicksPerMinute = TimeSpan.TicksPerMinute;

    /// <summary>Interprets only supported complete coverage; coarse presence and unknown dwell remain unknown.</summary>
    public static PlanningPlacements Resolve(PlanningEntry entry, string? zone = null, bool civil = false)
    {
        var timing = entry.When;
        if (timing.Form == "period" && timing.Start?.Value is { } start && timing.End?.Value is { } end)
        {
            if (start.Precision == "dateTime" && end.Precision == "dateTime")
                return Combine(Points(start, zone, civil), Points(end, zone, civil));
            if (entry.Kind is "window" or "stay" && start.Precision == end.Precision && start.Precision is "date" or "month")
                return Combine(Granule(start, false, zone), Granule(end, entry.Kind == "window", zone));
            return Unknown("Mixed/coarse occupied intervals are unsupported; refine only if needed.");
        }
        if (timing.Form == "at" && timing.Value is { } at)
        {
            if (entry.Kind == "window" && at.Precision is "date" or "month")
                return Combine(Granule(at, false, zone), Granule(at, true, zone));
            if (at.Precision != "dateTime" || entry.AllocationMinutes == null)
                return Unknown("Whole-entry coverage needs an end or explicit elapsed allocation; a date is not occupation.");
            var points = Points(at, zone, civil);
            return points with { Values = points.Values.Select(p => p with { End = p.Start + (long)entry.AllocationMinutes * TicksPerMinute }).ToArray() };
        }
        return Unknown("Coverage needs supported known bounds; missing, unknown and flexible values are not zero.");
    }

    /// <summary>A release uses stored End or known At allocation; arrival uses Start/At, never stored End as a deadline.</summary>
    public static PlanningPlacements Endpoint(PlanningEntry entry, bool release, string? zone = null, PlanningValue? whatIf = null)
    {
        if (whatIf != null) return Points(whatIf, zone, false);
        var timing = entry.When;
        if (timing.Form == "period")
        {
            if (timing.Start?.Value is { Precision: "dateTime" } && timing.End?.Value is { Precision: "dateTime" })
            {
                var coverage = Resolve(entry, zone);
                if (coverage.Inconsistent) return coverage;
            }
            var value = release ? timing.End?.Value : timing.Start?.Value;
            return value == null ? Unknown(release ? "Release/end is unknown." : "Arrival/start is unknown.") : Points(value, zone, false);
        }
        if (timing.Form == "at" && timing.Value is { } at)
        {
            if (release && entry.AllocationMinutes == null) return Unknown("Dwell/allocation is unknown; At is not a release.");
            var points = Points(at, zone, false);
            var extra = release ? (long)entry.AllocationMinutes!.Value * TicksPerMinute : 0;
            return points with { Values = points.Values.Select(p => new PlanningInterval(p.Start + extra, p.Start + extra, p.Frame)).ToArray() };
        }
        return Unknown("A comparable minute release/arrival is needed.");
    }

    /// <summary>Unresolved folds retain both mappings; no machine/geographic zone or lenient gap mapping.</summary>
    private static PlanningPlacements Points(PlanningValue value, string? interpretedZone, bool civil)
    {
        if (value.Precision != "dateTime") return Unknown("Elapsed comparison needs minute precision and a relevant zone.");
        var parsed = Minute.Parse(value.Local);
        if (!parsed.Success || parsed.Value.Year is < 1 or > 9999) return Unknown("Current civil value cannot be resolved.", true);
        var zoneId = value.Zone ?? interpretedZone;
        if (zoneId == null)
            return civil ? new([new(parsed.Value.ToDateTimeUnspecified().Ticks, parsed.Value.ToDateTimeUnspecified().Ticks, "civil")])
                : Unknown("A necessary zone is unspecified; choose one for this check or leave unresolved.");
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(zoneId);
        if (zone == null) return Unknown("The zone is unsupported by current rules.");
        var mapping = zone.MapLocal(parsed.Value);
        if (mapping.Count == 0) return Unknown("Current rules place this clock in a zone gap.");
        var instants = new[] { mapping.First(), mapping.Last() }
            .Where(z => value.SelectedOffsetSeconds == null || z.Offset.Seconds == value.SelectedOffsetSeconds)
            .Select(z => z.ToInstant().ToUnixTimeTicks()).Distinct().ToArray();
        return instants.Length == 0 ? Unknown("The selected fold offset is unavailable under current rules.")
            : new(instants.Select(t => new PlanningInterval(t, t, "instant")).ToArray());
    }

    /// <summary>Window final granules are inclusive; stay checkout is exclusive. Edges are calculations only.</summary>
    private static PlanningPlacements Granule(PlanningValue value, bool after, string? zone)
    {
        var pattern = value.Precision == "month" ? LocalDatePattern.CreateWithInvariantCulture("uuuu-MM") : LocalDatePattern.Iso;
        var parsed = pattern.Parse(value.Local);
        if (!parsed.Success || parsed.Value.Year is < 1 or > 9999) return Unknown("Invalid civil window.", true);
        var date = parsed.Value;
        var days = after ? value.Precision == "month" ? CalendarSystem.Iso.GetDaysInMonth(date.Year, date.Month) : 1 : 0;
        if (zone == null)
        {
            var ticks = date.AtMidnight().ToDateTimeUnspecified().Ticks + (long)days * TimeSpan.TicksPerDay;
            return new([new(ticks, ticks, "civil")]);
        }
        if (date.Year == 9999 && date.Month == 12 && days > 0 && date.Day + days > 31)
            return Unknown("Zoned coverage beyond year 9999 is unsupported.");
        var edge = date.PlusDays(days).AtMidnight();
        return Points(new("dateTime", Minute.Format(edge)), zone, false);
    }

    /// <summary>Rejects inconsistent or partly reversed placements rather than pruning inconvenient possibilities.</summary>
    private static PlanningPlacements Combine(PlanningPlacements starts, PlanningPlacements ends)
    {
        if (starts.Problem != null) return starts;
        if (ends.Problem != null) return ends;
        if (starts.Values.Any(s => ends.Values.Any(e => e.Frame != s.Frame))) return Unknown("The endpoints have no common frame.");
        var intervals = starts.Values.SelectMany(s => ends.Values.Select(e => new PlanningInterval(s.Start, e.End, s.Frame))).ToArray();
        if (intervals.All(i => i.End < i.Start)) return Unknown("The interval is internally reversed.", true);
        if (intervals.Any(i => i.End < i.Start)) return Unknown("Unresolved occurrences permit reversed ordering; choose the relevant fold.");
        return new(intervals);
    }

    /// <summary>Unknown carries a reason; inconsistency is never an ordinary interval.</summary>
    private static PlanningPlacements Unknown(string reason, bool inconsistent = false) => new([], reason, inconsistent);
}
