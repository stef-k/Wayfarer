using System.Globalization;
using Microsoft.Extensions.Primitives;
using NodaTime;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>One immutable private civil selection backed by the existing qualified history predicate.</summary>
internal sealed record TimelineCalendarScope(string DateType, DateOnly Date, HistoryQuery Query)
{
    /// <summary>Extended POST selections retain anchors separately from inclusive custom bounds.</summary>
    internal bool Extended { get; init; }
    internal int? WeekStartsOn { get; init; }
    internal DateOnly? CustomThrough { get; init; }
    internal static readonly string[] FilterKeys = ["q", "activityIds", "includeUnassigned", "west", "south", "east", "north"];
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

    /// <summary>Validate private form transport, then project into the single qualified history grammar.</summary>
    internal static TimelineCalendarScope ParsePost(IQueryCollection values, string operation, Instant now,
        HistoryDerivationEnvironment? environment = null)
    {
        var selectionKeys = operation == "availability" ? new[] { "year", "month", "timeBasis", "viewerZone" }
            : CalendarKeys.Concat(["weekStartsOn", "fromDate", "throughDate"]).ToArray();
        var controls = operation == "analysis" ? AnalysisKeys : operation == "records" ? ["pageSize", "continuation"] : Array.Empty<string>();
        HistoryQuery.ValidateKeys(values, selectionKeys.Concat(FilterKeys).Concat(controls));
        var zone = HistoryQuery.ParseZone(values["viewerZone"].FirstOrDefault());
        var basis = values["timeBasis"].FirstOrDefault();
        var type = operation == "availability" ? "month" : values["dateType"].FirstOrDefault();
        if (basis is not ("capture" or "viewer") || type is not ("day" or "week" or "month" or "year" or "custom")) throw Invalid();
        int Part(string key) => int.TryParse(values[key], NumberStyles.None, Invariant, out var n) ? n : throw Invalid();
        DateOnly Iso(string key) => DateOnly.TryParseExact(values[key], "yyyy-MM-dd", Invariant, DateTimeStyles.None, out var d) ? d : throw Invalid();
        var custom = type == "custom";
        if (custom && new[] { "year", "month", "day", "weekStartsOn" }.Any(values.ContainsKey)
            || !custom && new[] { "fromDate", "throughDate" }.Any(values.ContainsKey)
            || type != "week" && values.ContainsKey("weekStartsOn")) throw Invalid();
        DateOnly anchor;
        try { anchor = custom ? Iso("fromDate") : new(Part("year"), Part("month"), operation == "availability" ? 1 : Part("day")); }
        catch (ArgumentOutOfRangeException) { throw Invalid(); }
        int? week = type == "week" ? Part("weekStartsOn") : null;
        if (week is < 1 or > 7) throw Invalid();
        var start = type == "week" ? anchor.AddDays(-(((int)anchor.DayOfWeek + 6) % 7 + 1 - week!.Value + 7) % 7) : PeriodStart(type, anchor);
        var through = custom ? Iso("throughDate") : type == "week" ? start.AddDays(6)
            : type == "month" ? start.AddMonths(1).AddDays(-1) : type == "year" ? start.AddYears(1).AddDays(-1) : start;
        if (start > through || start.Year < 1900 || through.Year > 2100) throw Invalid();
        var projected = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["timeBasis"] = basis, ["timeMode"] = "days", ["viewerZone"] = zone.Id,
            ["fromDate"] = start.ToString("yyyy-MM-dd", Invariant), ["throughDate"] = through.ToString("yyyy-MM-dd", Invariant)
        };
        foreach (var key in FilterKeys.Concat(operation == "records" ? controls : []))
            if (values.ContainsKey(key)) projected[key] = values[key];
        var scope = new TimelineCalendarScope(type, anchor, HistoryQuery.Parse(new QueryCollection(projected), false, environment))
            { Extended = true, WeekStartsOn = week, CustomThrough = custom ? through : null };
        if (operation != "availability" && scope.Start > scope.Horizon(now)) throw Invalid();
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

    internal DateOnly Start => DateOnly.FromDateTime(Query.Start);
    internal DateOnly Through => DateOnly.FromDateTime(Query.ThroughDate);

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
            if (unit == "day" || unit == "week") target = Date.AddDays(direction * (unit == "week" ? 7 : 1));
            else if (unit == "custom") target = Date.AddDays(direction * (Through.DayNumber - Start.DayNumber + 1));
            else
            {
                var first = new DateOnly(Date.Year, Date.Month, 1);
                first = unit == "month" ? first.AddMonths(direction) : first.AddYears(direction);
                target = new(first.Year, first.Month, Math.Min(Date.Day, DateTime.DaysInMonth(first.Year, first.Month)));
            }
        }
        catch (ArgumentOutOfRangeException) { return null; }
        if (target.Year is < 1900 or > 2100) return null;
        if (!Extended) return Create(DateType, target, Query.Scope.TimeBasis, Query.Zone.Id);
        var values = new Dictionary<string, StringValues>
        {
            ["dateType"] = DateType, ["year"] = target.Year.ToString(Invariant),
            ["month"] = target.Month.ToString(Invariant), ["day"] = target.Day.ToString(Invariant),
            ["timeBasis"] = Query.Scope.TimeBasis, ["viewerZone"] = Query.Zone.Id
        };
        if (WeekStartsOn != null) values["weekStartsOn"] = WeekStartsOn.Value.ToString(Invariant);
        if (DateType == "custom")
        {
            values.Remove("year"); values.Remove("month"); values.Remove("day");
            values["fromDate"] = target.ToString("yyyy-MM-dd", Invariant);
            try { values["throughDate"] = target.AddDays(Through.DayNumber - Start.DayNumber).ToString("yyyy-MM-dd", Invariant); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        try { return ParsePost(new QueryCollection(values), "period", Instant.FromUtc(2100, 12, 31, 0, 0)); }
        catch (HistoryRequestException) { return null; }
    }

    /// <summary>UI navigation policy does not assert absence of future stored records.</summary>
    internal object Navigation(Instant now)
    {
        var horizon = Horizon(now);
        var next = Move(DateType, 1);
        return new { today = DateOnly.FromDateTime(now.InZone(Query.Zone).ToDateTimeUnspecified()), horizon,
            previous = Move(DateType, -1)?.Selection, next = next != null && next.Start <= (Extended ? horizon : PeriodStart(DateType, horizon)) ? next.Selection : null };
    }

    /// <summary>Echo the selected civil components and canonical frozen zone separately from predicate boundaries.</summary>
    internal object Selection
    {
        get
        {
            var value = new Dictionary<string, object?> { ["dateType"] = DateType,
                ["timeBasis"] = Query.Scope.TimeBasis, ["viewerZone"] = Query.Zone.Id };
            if (DateType == "custom") { value["fromDate"] = Date.ToString("yyyy-MM-dd", Invariant); value["throughDate"] = CustomThrough?.ToString("yyyy-MM-dd", Invariant); }
            else { value["year"] = Date.Year; value["month"] = Date.Month; value["day"] = Date.Day; }
            if (WeekStartsOn != null) value["weekStartsOn"] = WeekStartsOn;
            return value;
        }
    }

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
