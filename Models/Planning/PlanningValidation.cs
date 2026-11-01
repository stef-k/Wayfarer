using NodaTime;
using NodaTime.Text;

namespace Wayfarer.Models.Planning;

/// <summary>Safe field validation; submitted civil values are never reflected into errors.</summary>
public sealed class PlanningValidationException(string message) : Exception(message);

/// <summary>Validates structured intentions without imposing parent, overlap, presence or travel constraints.</summary>
public static class PlanningValidation
{
    /// <summary>Bounds one object aggregate; larger plans use existing geography rather than truncated values.</summary>
    public const int MaximumEntries = 128;
    private static readonly LocalDatePattern Month = LocalDatePattern.CreateWithInvariantCulture("uuuu-MM");
    private static readonly LocalDatePattern Date = LocalDatePattern.Iso;
    private static readonly LocalDateTimePattern Minute = LocalDateTimePattern.CreateWithInvariantCulture("uuuu-MM-dd'T'HH:mm");

    /// <summary>Unchanged trusted endpoints survive TZDB changes; new or changed endpoints use current rules.</summary>
    public static void Validate(PlanningAggregate value, string targetKind, PlanningAggregate? original = null)
    {
        Require(value.Format == 1 && value.Entries is { Count: > 0 and <= MaximumEntries }, "Provide 1–128 planning entries in format 1.");
        HashSet<Guid> ids = [];
        foreach (var entry in value.Entries)
        {
            Require(entry != null && entry.Id != Guid.Empty && ids.Add(entry.Id), "Planning entry identities must be distinct and nonempty.");
            Require(entry.Label == null || entry.Label.Length <= 200 && !entry.Label.Contains('\0'), "A planning label may contain at most 200 characters.");
            Require(entry.Kind == null || entry.Kind == "window" || entry.Kind is "presence" or "stay" && targetKind != "segment"
                || entry.Kind == "travel" && targetKind is "trip" or "segment", "Choose a supported meaning for this object.");
            var timing = entry.When;
            Require(timing != null && timing.Form is "at" or "period" or "unspecified" or "unknown" or "flexible", "Choose a supported timing form.");
            Require(entry.AllocationMinutes == null || entry.AllocationMinutes >= 0 && entry.Kind is not ("window" or "stay") && timing.Form != "period",
                "Elapsed allocation must be a nonnegative integer on an At or undated intention.");
            var before = original?.Entries.SingleOrDefault(item => item.Id == entry.Id)?.When;
            if (timing.Form == "at")
            {
                Require(timing.Value != null && timing.Start == null && timing.End == null, "At requires one value and no period bounds.");
                ValidateValue(timing.Value, before?.Value);
            }
            else if (timing.Form == "period")
            {
                Require(timing.Value == null && (timing.Start?.State == "known" || timing.End?.State == "known"), "A period requires at least one known bound.");
                ValidateBound(timing.Start, before?.Start);
                ValidateBound(timing.End, before?.End);
                // Historical unchanged periods retain their exact intention after a zone-rule update.
                if (timing != before && timing.Start?.Value is { } start && timing.End?.Value is { } end)
                    Require(!Reversed(start, end), "The period end is provably before its start. Retain and correct the draft.");
            }
            else Require(timing.Value == null && timing.Start == null && timing.End == null, "Undated timing cannot contain values or bounds.");
        }
    }

    /// <summary>Missing, unknown and flexible bounds do not acquire a fabricated value.</summary>
    private static void ValidateBound(PlanningBound? bound, PlanningBound? before)
    {
        if (bound == null) return;
        Require(bound.State is "known" or "unspecified" or "unknown" or "flexible", "Choose a supported bound state.");
        Require((bound.State == "known") == (bound.Value != null), "Only a known bound contains a value.");
        if (bound.Value != null) ValidateValue(bound.Value, before?.Value);
    }

    /// <summary>Strict round-trip patterns prevent accepting seconds, truncation, invalid dates or year zero.</summary>
    private static void ValidateValue(PlanningValue value, PlanningValue? before)
    {
        Require(value.Local != null, "Supply civil components at the selected precision.");
        if (value.Precision is "month" or "date")
        {
            var pattern = value.Precision == "month" ? Month : Date;
            var parsed = pattern.Parse(value.Local);
            Require(parsed.Success && parsed.Value.Year is >= 1 and <= 9999 && pattern.Format(parsed.Value) == value.Local
                && value.Zone == null && value.SelectedOffsetSeconds == null, "Supply a valid month or date without a clock zone.");
            return;
        }
        Require(value.Precision == "dateTime", "Choose month, date or minute date-time precision.");
        var minute = Minute.Parse(value.Local);
        Require(minute.Success && minute.Value.Year is >= 1 and <= 9999 && Minute.Format(minute.Value) == value.Local, "Supply a valid date and minute clock, without seconds.");
        Require(value.Zone != null || value.SelectedOffsetSeconds == null, "An occurrence offset requires an explicit zone.");
        if (value.Zone == null || value == before) return;
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(value.Zone);
        Require(zone != null, "Choose UTC or a supported IANA zone.");
        var mapping = zone.MapLocal(minute.Value);
        Require(mapping.Count > 0, "This clock falls in a zone gap; choose a valid civil time.");
        Require(value.SelectedOffsetSeconds == null || mapping.First().Offset.Seconds == value.SelectedOffsetSeconds
            || mapping.Last().Offset.Seconds == value.SelectedOffsetSeconds, "The selected occurrence offset does not match this zone and clock.");
    }

    /// <summary>Uses resolved instants before civil ordering; differing unresolved frames prove nothing.</summary>
    private static bool Reversed(PlanningValue start, PlanningValue end)
    {
        if (start.Precision == "dateTime" && end.Precision == "dateTime")
        {
            if (start.Zone == null && end.Zone == null)
                return Minute.Parse(end.Local).Value < Minute.Parse(start.Local).Value;
            if (start.Zone == null || end.Zone == null) return false;
            var first = Instants(start);
            var last = Instants(end);
            return first.Length > 0 && last.Length > 0 && last.Max() < first.Min();
        }
        // Unzoned civil components can prove nominal ordering across precision levels.
        // A zoned clock has no established common frame with an unzoned calendar granule.
        if (start.Zone != null || end.Zone != null) return false;
        return CivilEdge(end, latest: true) < CivilEdge(start, latest: false);
    }

    /// <summary>Transient granule edges support ordering without persisting invented civil components.</summary>
    private static LocalDateTime CivilEdge(PlanningValue value, bool latest)
    {
        if (value.Precision == "dateTime") return Minute.Parse(value.Local).Value;
        var date = value.Precision == "month" ? Month.Parse(value.Local).Value : Date.Parse(value.Local).Value;
        if (!latest) return date.AtMidnight();
        if (value.Precision == "month") date = new(date.Year, date.Month, CalendarSystem.Iso.GetDaysInMonth(date.Year, date.Month));
        return date.At(new LocalTime(23, 59, 59, 999).PlusNanoseconds(999999));
    }

    /// <summary>Reads remain lossless after TZDB changes; diagnostics invite explicit refinement without rewriting values.</summary>
    public static IReadOnlyList<PlanningDiagnostic> Diagnostics(PlanningAggregate? aggregate, string kind)
    {
        if (aggregate == null) return [];
        List<PlanningDiagnostic> diagnostics = [];
        foreach (var entry in aggregate.Entries)
        {
            try { Validate(new(aggregate.Format, [entry]), kind); }
            catch (PlanningValidationException error) { diagnostics.Add(new(entry.Id, "planning-current-rules-review", error.Message)); }
        }
        return diagnostics;
    }

    /// <summary>Returns all admissible occurrences, retaining an unresolved fold as two possibilities.</summary>
    private static Instant[] Instants(PlanningValue value)
    {
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(value.Zone!);
        if (zone == null) return [];
        var mapping = zone.MapLocal(Minute.Parse(value.Local).Value);
        if (mapping.Count == 0) return [];
        return new[] { mapping.First(), mapping.Last() }.Where(item => value.SelectedOffsetSeconds == null
            || item.Offset.Seconds == value.SelectedOffsetSeconds).Select(item => item.ToInstant()).Distinct().ToArray();
    }

    /// <summary>One safe validation failure, leaving transaction and draft ownership to the caller.</summary>
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new PlanningValidationException(message);
    }
}

/// <summary>Owner-only current-rule diagnostic; neither persisted state nor a Check plan result.</summary>
public sealed record PlanningDiagnostic(Guid EntryId, string Code, string Message);
