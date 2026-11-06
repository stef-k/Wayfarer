using System.Globalization;
using Microsoft.Extensions.Primitives;
using NodaTime;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>One immutable private civil selection backed by the existing qualified history predicate.</summary>
internal sealed record TimelineCalendarScope(string DateType, DateOnly Date, HistoryQuery Query)
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly string[] CalendarKeys = ["dateType", "year", "month", "day", "timeBasis", "viewerZone"];
    internal static readonly string[] AnalysisKeys = ["maximumGapSeconds", "speedEnabled", "maximumSpeedMetresPerSecond", "includeGeometry"];

    /// <summary>Explicit frozen zone is mandatory; absent date seeds viewer Today even for the capture default.</summary>
    internal static TimelineCalendarScope Parse(IQueryCollection values, bool analysis, Instant now,
        HistoryDerivationEnvironment? environment = null)
    {
        HistoryQuery.ValidateKeys(values, analysis ? CalendarKeys.Concat(AnalysisKeys) : CalendarKeys);
        var zone = HistoryQuery.ParseZone(values["viewerZone"].FirstOrDefault());
        var basis = values["timeBasis"].FirstOrDefault() ?? "capture";
        var type = values["dateType"].FirstOrDefault() ?? "day";
        if (basis is not ("capture" or "viewer") || type is not ("day" or "month" or "year")) throw Invalid();
        var today = DateOnly.FromDateTime(now.InZone(zone).LocalDateTime.ToDateTimeUnspecified());
        int Part(string key, int fallback) => values.ContainsKey(key)
            ? int.TryParse(values[key], NumberStyles.None, Invariant, out var number) ? number : throw Invalid() : fallback;
        var year = Part("year", today.Year);
        var month = Part("month", values.ContainsKey("year") ? 1 : today.Month);
        var day = Part("day", values.ContainsKey("year") || values.ContainsKey("month") ? 1 : today.Day);
        if (year is < 1900 or > 2100 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) throw Invalid();
        var scope = Create(type, new(year, month, day), basis, zone.Id, environment);
        var horizon = scope.Horizon(now);
        if (scope.Start > PeriodStart(type, horizon)) throw Invalid();
        return scope;
    }

    /// <summary>Calendar successors are wall boundaries, never fixed elapsed days or host-local dates.</summary>
    private static TimelineCalendarScope Create(string type, DateOnly date, string basis, string zone,
        HistoryDerivationEnvironment? environment = null)
    {
        var start = PeriodStart(type, date);
        var end = type == "day" ? start.AddDays(1) : type == "month" ? start.AddMonths(1) : start.AddYears(1);
        var values = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["timeBasis"] = basis, ["timeMode"] = "days", ["viewerZone"] = zone,
            ["fromDate"] = start.ToString("yyyy-MM-dd", Invariant),
            ["throughDate"] = end.AddDays(-1).ToString("yyyy-MM-dd", Invariant)
        });
        return new(type, date, HistoryQuery.Parse(values, false, environment));
    }

    internal DateOnly Start => PeriodStart(DateType, Date);

    /// <summary>Viewer Today and capture's conservative latest possible date share the serving offset authority.</summary>
    internal DateOnly Horizon(Instant now) => DateOnly.FromDateTime(Query.Scope.TimeBasis == "viewer"
        ? now.InZone(Query.Zone).LocalDateTime.ToDateTimeUnspecified()
        : now.WithOffset(Offset.FromSeconds(HistoryPredicate.MaxCaptureOffset)).LocalDateTime.ToDateTimeUnspecified());

    /// <summary>Clamp the civil day before constructing another month/year; empty past periods remain reachable.</summary>
    internal TimelineCalendarScope? Move(string unit, int direction)
    {
        DateOnly target;
        try
        {
            if (unit == "day") target = Date.AddDays(direction);
            else
            {
                var first = new DateOnly(Date.Year, Date.Month, 1);
                first = unit == "month" ? first.AddMonths(direction) : first.AddYears(direction);
                target = new(first.Year, first.Month, Math.Min(Date.Day, DateTime.DaysInMonth(first.Year, first.Month)));
            }
        }
        catch (ArgumentOutOfRangeException) { return null; }
        return target.Year is < 1900 or > 2100 ? null : Create(DateType, target, Query.Scope.TimeBasis, Query.Zone.Id);
    }

    /// <summary>UI navigation policy does not assert absence of future stored records.</summary>
    internal object Navigation(Instant now)
    {
        var horizon = Horizon(now);
        var next = Move(DateType, 1);
        return new { today = DateOnly.FromDateTime(now.InZone(Query.Zone).ToDateTimeUnspecified()), horizon,
            previous = Move(DateType, -1)?.Selection, next = next != null && next.Start <= PeriodStart(DateType, horizon) ? next.Selection : null };
    }

    /// <summary>Echo the selected civil components and canonical frozen zone separately from predicate boundaries.</summary>
    internal object Selection => new { dateType = DateType, year = Date.Year, month = Date.Month, day = Date.Day,
        timeBasis = Query.Scope.TimeBasis, viewerZone = Query.Zone.Id };

    private static DateOnly PeriodStart(string type, DateOnly date) => type == "year" ? new(date.Year, 1, 1)
        : type == "month" ? new(date.Year, date.Month, 1) : date;
    private static HistoryRequestException Invalid() => new("parametersInvalid");
}

/// <summary>Exact binary64 analytical rules; Off removes only the speed heuristic.</summary>
internal sealed record TimelineConnectionRules(double MaximumGapSeconds, double? MaximumSpeedMetresPerSecond)
{
    /// <summary>Reject invalid finite controls without rounding, quotas, clamping or silent defaults for supplied input.</summary>
    internal static (TimelineConnectionRules Rules, bool Geometry) Parse(IQueryCollection values)
    {
        double Number(string key, double fallback) => !values.ContainsKey(key) ? fallback
            : double.TryParse(values[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && double.IsFinite(value) && value > 0 ? value : throw new HistoryRequestException("parametersInvalid");
        bool Boolean(string key, bool fallback) => !values.ContainsKey(key) ? fallback
            : bool.TryParse(values[key], out var value) ? value : throw new HistoryRequestException("parametersInvalid");
        var gap = Number("maximumGapSeconds", 7200);
        var speed = Number("maximumSpeedMetresPerSecond", 350);
        return (new(gap, Boolean("speedEnabled", true) ? speed : null), Boolean("includeGeometry", false));
    }
}
