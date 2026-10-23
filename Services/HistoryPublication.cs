using System.Text;
using NodaTime;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>Explicit wire facts; internal search/preparation fields and credentials never enter a publication.</summary>
internal static class HistoryPublication
{
    internal static object Versions => new { time = LocationHistoryDerivation.TimeVersion, text = LocationHistoryDerivation.TextVersion,
        interpretation = HistoryCursor.Rules };

    /// <summary>Only bounded selected-row previews cross the SQL boundary, and truncation is explicit.</summary>
    internal static Dictionary<string, object?> Summary(HistorySummaryRow row, DateTimeZone zone, bool candidate)
    {
        var result = new Dictionary<string, object?>
        {
            ["locationId"] = row.Id, ["uuid"] = row.Uuid?.ToString("D"),
            ["eventUtc"] = row.EventUtc is { } instant ? HistoryQuery.Utc(instant) : new { status = "unavailable", reason = "invalidEventUtc" } as object,
            ["position"] = Position(row.Longitude, row.Latitude),
            ["captureTime"] = Capture(row.CaptureLocal, row.CaptureZone, row.CaptureOffset, row.TimeStatus, row.TimeVersion, row.TimeFailure),
            ["viewerTime"] = Viewer(row.EventUtc, zone), ["activityTypeId"] = row.ActivityTypeId,
            ["activityNamePreview"] = Preview(row.ActivityName), ["labelPreview"] = Preview(row.Label)
        };
        if (candidate)
        {
            result["filterMembership"] = "undetermined";
            result["reasonCodes"] = new[] { row.EventUtc == null ? "invalidEventUtc" : null,
                row.TimeStatus == "unresolved" ? SafeTimeReason(row.TimeFailure) : null,
                row.Longitude == null ? "invalidCoordinates" : null }.Where(reason => reason != null).Distinct().ToArray();
        }
        return result;
    }

    /// <summary>Known-null previews remain null; any returned text is a plain, scalar-bounded label.</summary>
    private static object Preview(string? value)
    {
        var scalars = (value ?? "").EnumerateRunes().ToArray();
        return new { text = value == null ? null : string.Concat(scalars.Take(160).Select(rune => rune.ToString())), truncated = scalars.Length > 160 };
    }

    internal static object Position(double? longitude, double? latitude) => longitude is { } x && latitude is { } y
        ? new { status = "available", longitude = x, latitude = y } as object
        : new { status = "unavailable", reason = "invalidCoordinates" };

    /// <summary>Prepared capture facts retain their generation; stale/missing preparation never becomes invented evidence.</summary>
    internal static object Capture(DateTime? local, string? zone, int? offset, string? status, string? version, string? failure)
    {
        if (version != LocationHistoryDerivation.TimeVersion)
            return new { status = "pendingPreparation", reason = "historyPreparationRequired", version };
        if (status == "resolved" && local != null && zone != null && offset != null)
            return new { status = "available", local = HistoryQuery.Local(local.Value), zoneId = zone, offsetSeconds = offset, version };
        return new { status = "unavailable", reason = SafeTimeReason(failure), version };
    }

    /// <summary>Viewer facts use the very same serving rules as the SQL offset segments.</summary>
    internal static object Viewer(DateTime? eventUtc, DateTimeZone zone)
    {
        if (eventUtc == null) return new { status = "unavailable", reason = "invalidEventUtc", version = LocationHistoryDerivation.TimeVersion };
        try
        {
            var value = Instant.FromDateTimeUtc(eventUtc.Value).InZone(zone);
            return new { status = "available", local = HistoryQuery.Local(value.LocalDateTime.ToDateTimeUnspecified()), zoneId = zone.Id,
                offsetSeconds = value.Offset.Seconds, version = LocationHistoryDerivation.TimeVersion };
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
        { return new { status = "unavailable", reason = "viewerOutOfRange", version = LocationHistoryDerivation.TimeVersion }; }
    }

    /// <summary>Defensive allowlist prevents retained/tool text from becoming a public failure reason.</summary>
    private static string SafeTimeReason(string? reason) => reason is "invalidCoordinates" or "invalidEventUtc" or "zoneUnavailable" or "captureOutOfRange" ? reason : "captureUnavailable";
}

/// <summary>Selected SQL facts only; long Notes and owner device metadata are never fetched for list filtering.</summary>
internal sealed class HistorySummaryRow
{
    public int Id { get; set; }
    public Guid? Uuid { get; set; }
    public DateTime? EventUtc { get; set; }
    public double? Longitude { get; set; }
    public double? Latitude { get; set; }
    public DateTime? CaptureLocal { get; set; }
    public string? CaptureZone { get; set; }
    public int? CaptureOffset { get; set; }
    public string? TimeStatus { get; set; }
    public string? TimeVersion { get; set; }
    public string? TimeFailure { get; set; }
    public int? ActivityTypeId { get; set; }
    public string? ActivityName { get; set; }
    public string? Label { get; set; }
}
