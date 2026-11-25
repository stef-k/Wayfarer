using System.Globalization;
using System.Text.RegularExpressions;

namespace Wayfarer.Localization;

/// <summary>Projects established Quartz states and stored summaries into the Admin Jobs document.</summary>
internal static class AdminJobWebText
{
    private static readonly (Regex Pattern, string Key)[] FileAndEntrySummaries =
    [
        (SummaryPattern("Deleted ([0-9]+) old log files"), "AdminJobs.OldLogFiles"),
        (SummaryPattern("Deleted ([0-9]+) audit logs older than 2 years"), "AdminJobs.OldAuditLogs"),
        (SummaryPattern("Deleted ([0-9]+) eligible Location receipts"), "AdminJobs.LocationReceipts"),
        (SummaryPattern("Removed ([0-9]+) expired entries"), "AdminJobs.ExpiredEntries")
    ];
    private static readonly Regex Visits = SummaryPattern("Closed ([0-9]+) visits, deleted ([0-9]+) candidates");

    /// <summary>Uses a known status label or a neutral fallback for an unrecognized machine value.</summary>
    internal static string Status(WebMessages text, string status) => text[status switch
    {
        "Not Started" => "AdminJobs.Status.NotStarted",
        "In Progress" => "AdminJobs.Status.InProgress",
        "Running" or "Paused" or "Scheduled" or "Completed" or "Failed" or "Cancelled" => $"AdminJobs.Status.{status}",
        _ => "AdminJobs.Status.Unknown"
    }];

    /// <summary>Translates the exact first-party summaries while retaining unfamiliar diagnostic text verbatim.</summary>
    internal static string Summary(WebMessages text, string stored)
    {
        foreach (var (pattern, key) in FileAndEntrySummaries)
        {
            var match = pattern.Match(stored);
            if (TryCount(match, 1, out var count))
                return text.Count(key, count);
        }

        var visits = Visits.Match(stored);
        return TryCount(visits, 1, out var closed) && TryCount(visits, 2, out var deleted)
            ? $"{text.Count("AdminJobs.VisitsClosed", closed)} {text.Count("AdminJobs.CandidatesDeleted", deleted)}"
            : stored;
    }

    /// <summary>Matches a full persisted summary and scans at most the input length.</summary>
    private static Regex SummaryPattern(string pattern) =>
        new($@"\A{pattern}\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>Reads a persisted count without applying the document display culture.</summary>
    private static bool TryCount(Match match, int group, out long count) =>
        long.TryParse(match.Groups[group].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out count);
}
