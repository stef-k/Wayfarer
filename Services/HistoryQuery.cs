using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NodaTime;
using NodaTime.TimeZones;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>Validated matching scope, separate from page/aggregation controls; serialized into scope identity.</summary>
internal sealed record HistoryScope(string TimeBasis, string TimeMode, string ViewerZone,
    string? StartUtc, string? EndUtc, string? FromLocal, string? UntilLocal,
    string? FromDate, string? ThroughDate, string? FromTime, string? UntilTime,
    string Q, int[]? ActivityIds, bool IncludeUnassigned, HistoryArea? Area);

/// <summary>Exact half-open rectangle; west greater than east wraps. The world maximum includes the north pole.</summary>
internal sealed record HistoryArea(double West, double South, double East, double North)
{
    internal double Span => West < East ? East - West : 360 - West + East;
}

/// <summary>One request definition used by page, overview and conservative uncertainty SQL.</summary>
internal sealed class HistoryQuery
{
    internal required HistoryScope Scope { get; set; }
    internal required DateTimeZone Zone { get; init; }
    internal DateTime Start { get; init; }
    internal DateTime End { get; init; }
    internal DateTime ThroughDate { get; init; }
    internal TimeSpan FromTime { get; init; }
    internal TimeSpan UntilTime { get; init; }
    internal int PageSize { get; init; } = 100;
    internal int Grid { get; init; } = 16;
    internal string ResultSet { get; init; } = "matches";
    internal string? Continuation { get; init; }
    internal bool Overnight => FromTime > UntilTime;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly string[] Common = ["timeBasis", "timeMode", "viewerZone", "startUtc", "endUtc", "fromLocal", "untilLocal",
        "fromDate", "throughDate", "fromTime", "untilTime", "q", "activityIds", "includeUnassigned", "west", "south", "east", "north"];

    /// <summary>Rejects unknown/repeated/conflicting values, without implicit host time, rounding or range caps.</summary>
    internal static HistoryQuery Parse(IQueryCollection values, bool overview, HistoryDerivationEnvironment? environment = null)
    {
        ValidateKeys(values, Common.Concat(overview ? ["grid"] : new[] { "pageSize", "continuation", "resultSet" }));
        string? Get(string key) => values.TryGetValue(key, out var v) ? v[0] : null;
        var basis = Get("timeBasis"); var mode = Get("timeMode");
        if (basis is not ("utc" or "capture" or "viewer") || mode is not ("interval" or "days" or "daily")
            || basis == "utc" && mode != "interval") throw Invalid();
        environment ??= HistoryDerivationEnvironment.Current;
        environment.Require(basis != "utc", false);
        var zone = ParseZone(Get("viewerZone"));
        var timeKeys = basis == "utc" ? new[] { "startUtc", "endUtc" }
            : mode == "interval" ? ["fromLocal", "untilLocal"]
            : mode == "days" ? ["fromDate", "throughDate"] : new[] { "fromDate", "throughDate", "fromTime", "untilTime" };
        foreach (var key in Common.Skip(3).Take(8))
            if (values.ContainsKey(key) != timeKeys.Contains(key)) throw Invalid();
        DateTime start, end;
        DateTime through = default;
        TimeSpan from = default, until = default;
        if (mode == "interval")
        {
            start = ParseDateTime(Get(timeKeys[0]), basis == "utc");
            end = ParseDateTime(Get(timeKeys[1]), basis == "utc");
        }
        else
        {
            start = ParseDate(Get("fromDate"));
            through = ParseDate(Get("throughDate"));
            if (through < start) throw Invalid();
            if (mode == "daily")
            {
                from = ParseTime(Get("fromTime")); until = ParseTime(Get("untilTime"));
                if (from == until) throw Invalid();
                if (from > until && through == DateTime.MaxValue.Date) throw Invalid();
                end = (from > until ? through.AddDays(1) : through).Add(until);
            }
            else end = through == DateTime.MaxValue.Date ? throw Invalid() : through.AddDays(1);
        }
        if (start >= end) throw Invalid();
        var rawText = Get("q") ?? "";
        if (rawText.EnumerateRunes().Count() > 256) throw Invalid();
        var text = LocationHistoryDerivation.NormalizeText(rawText, environment);
        int[]? ids = null;
        if (values.ContainsKey("activityIds"))
        {
            var parts = Get("activityIds")!.Split(',');
            ids = parts.Select(value => PositiveInt(value, int.MaxValue)).Distinct().Order().ToArray();
        }
        var unassigned = false;
        if (values.ContainsKey("includeUnassigned") && !bool.TryParse(Get("includeUnassigned"), out unassigned)) throw Invalid();
        if (ids == null && values.ContainsKey("includeUnassigned") && !unassigned) throw Invalid();
        if (ids == null && unassigned) ids = [];
        var area = ParseArea(values);
        return new HistoryQuery
        {
            Scope = new(basis!, mode!, zone.Id,
                basis == "utc" ? Utc(start) : null, basis == "utc" ? Utc(end) : null,
                basis != "utc" && mode == "interval" ? Local(start) : null, basis != "utc" && mode == "interval" ? Local(end) : null,
                mode != "interval" ? start.ToString("yyyy-MM-dd", Invariant) : null,
                mode != "interval" ? through.ToString("yyyy-MM-dd", Invariant) : null,
                mode == "daily" ? Clock(from) : null, mode == "daily" ? Clock(until) : null,
                text, ids, unassigned, area),
            Zone = zone, Start = start, End = end, ThroughDate = through, FromTime = from, UntilTime = until,
            PageSize = Get("pageSize") is { } size ? PositiveInt(size, 500) : 100,
            Grid = Get("grid") is { } grid ? PositiveInt(grid, 16) : 16,
            ResultSet = Get("resultSet") is { } set ? set is "matches" or "unresolvedCandidates" ? set : throw Invalid() : "matches",
            Continuation = Get("continuation") is { } cursor ? cursor.Length is > 0 and <= 8192 && Encoding.UTF8.GetByteCount(cursor) <= 8192
                ? cursor : throw new HistoryRequestException("continuationInvalid") : null
        };
    }

    /// <summary>Detail accepts only its explicit viewer display zone.</summary>
    internal static DateTimeZone DetailZone(IQueryCollection values)
    {
        ValidateKeys(values, ["viewerZone"]);
        return ParseZone(values["viewerZone"].FirstOrDefault());
    }

    /// <summary>Pure wall predicate mirrors SQL; useful for boundary/segment qualification without a database matrix.</summary>
    internal bool ContainsWall(DateTime local)
    {
        if (Scope.TimeMode != "daily") return local >= Start && local < End;
        var anchor = local.Date;
        if (Overnight && local.TimeOfDay < UntilTime)
        {
            if (anchor == DateTime.MinValue.Date) return false;
            anchor = anchor.AddDays(-1);
        }
        return anchor >= Start && anchor <= ThroughDate && (Overnight
            ? local.TimeOfDay >= FromTime || local.TimeOfDay < UntilTime
            : local.TimeOfDay >= FromTime && local.TimeOfDay < UntilTime);
    }

    /// <summary>Canonical IANA identity comes from serving NodaTime data, never host or PostgreSQL TZDB.</summary>
    private static DateTimeZone ParseZone(string? value)
    {
        if (value == null || value.Length > 255 || !DateTimeZoneProviders.Tzdb.Ids.Contains(value)) throw Invalid();
        return DateTimeZoneProviders.Tzdb[TzdbDateTimeZoneSource.Default.CanonicalIdMap[value]];
    }

    /// <summary>Finite WGS84 bounds with explicit dateline and pole semantics.</summary>
    private static HistoryArea? ParseArea(IQueryCollection values)
    {
        string[] keys = ["west", "south", "east", "north"];
        if (!keys.Any(values.ContainsKey)) return null;
        if (!keys.All(values.ContainsKey)) throw Invalid();
        var numbers = keys.Select(key => double.TryParse(values[key][0], NumberStyles.Float, Invariant, out var n)
            && double.IsFinite(n) ? n : throw Invalid()).ToArray();
        var area = new HistoryArea(numbers[0], numbers[1], numbers[2], numbers[3]);
        if (area.West is < -180 or > 180 || area.East is < -180 or > 180 || area.South < -90 || area.North > 90
            || area.South >= area.North || area.West == area.East || area.Span <= 0) throw Invalid();
        return area;
    }

    /// <summary>Only microsecond ISO forms are accepted; UTC requires an explicit offset.</summary>
    private static DateTime ParseDateTime(string? value, bool utc)
    {
        var pattern = @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}"
            + (utc ? @":[0-9]{2}(\.[0-9]{1,6})?(Z|[+-][0-9]{2}:[0-9]{2})$" : @"(:[0-9]{2}(\.[0-9]{1,6})?)?$");
        if (value == null || !Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant)) throw Invalid();
        if (utc)
        {
            var zulu = value.EndsWith('Z');
            var wallText = zulu ? value[..^1] : value[..^6];
            if (!DateTime.TryParse(wallText, Invariant, DateTimeStyles.None, out var wall)) throw Invalid();
            var hours = zulu ? 0 : int.Parse(value.AsSpan(value.Length - 5, 2), Invariant);
            var minutes = zulu ? 0 : int.Parse(value.AsSpan(value.Length - 2, 2), Invariant);
            if (hours > 23 || minutes > 59) throw Invalid();
            var seconds = (hours * 3600L + minutes * 60L) * (!zulu && value[^6] == '-' ? -1 : 1);
            var ticks = wall.Ticks - seconds * TimeSpan.TicksPerSecond;
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) throw Invalid();
            return new DateTime(ticks, DateTimeKind.Utc);
        }
        if (!DateTime.TryParse(value, Invariant, DateTimeStyles.None, out var local)) throw Invalid();
        return DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
    }

    /// <summary>Calendar input is exact Gregorian ISO, without host culture or implicit time.</summary>
    private static DateTime ParseDate(string? value) => DateTime.TryParseExact(value, "yyyy-MM-dd", Invariant, DateTimeStyles.None, out var date) ? date : throw Invalid();
    /// <summary>Daily clocks remain exact to stored microsecond precision.</summary>
    private static TimeSpan ParseTime(string? value)
    {
        if (value == null || !Regex.IsMatch(value, @"^[0-9]{2}:[0-9]{2}(:[0-9]{2}(\.[0-9]{1,6})?)?$", RegexOptions.CultureInvariant)) throw Invalid();
        if (!TimeOnly.TryParse(value, Invariant, DateTimeStyles.None, out var time)) throw Invalid();
        return time.ToTimeSpan();
    }
    /// <summary>Finite request controls and catalog identities do not accept signs, whitespace or overflow.</summary>
    private static int PositiveInt(string value, int maximum) => int.TryParse(value, NumberStyles.None, Invariant, out var number) && number > 0 && number <= maximum ? number : throw Invalid();
    /// <summary>All parameter failures are controlled and omit supplied content.</summary>
    private static HistoryRequestException Invalid() => new("parametersInvalid");
    /// <summary>Canonical wire instants retain all stored microseconds.</summary>
    internal static string Utc(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", Invariant);
    /// <summary>Wall facts never acquire an invented offset suffix.</summary>
    internal static string Local(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", Invariant);
    /// <summary>Echoed clocks round-trip through the accepted microsecond grammar, without TimeSpan's seventh digit.</summary>
    private static string Clock(TimeSpan value) => TimeOnly.FromTimeSpan(value).ToString("HH:mm:ss.ffffff", Invariant);

    /// <summary>Every route uses one case-sensitive allowlist and rejects PostgreSQL-untransportable input.</summary>
    private static void ValidateKeys(IQueryCollection values, IEnumerable<string> allowed)
    {
        var keys = allowed.ToHashSet(StringComparer.Ordinal);
        if (values.Sum(pair => Encoding.UTF8.GetByteCount(pair.Key) + pair.Value.Sum(v => Encoding.UTF8.GetByteCount(v ?? ""))) > 8192) throw Invalid();
        foreach (var pair in values)
            if (!keys.Contains(pair.Key) || pair.Value.Count != 1 || !ActivityCatalog.CanTransport(pair.Value[0])) throw Invalid();
    }
}

/// <summary>Controlled contract failure; never carries a query, token or database message.</summary>
internal sealed class HistoryRequestException(string code, int status = 400) : Exception
{
    internal string Code { get; } = code;
    internal int Status { get; } = status;
}
