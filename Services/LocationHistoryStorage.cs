using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>Writes selected derived columns from an already locked final row; callers own the transaction.</summary>
internal static class LocationHistoryStorage
{
    /// <summary>Shared by preparation and both fenced ExecuteUpdate branches; never restores a canonical entity.</summary>
    internal static async Task UpdateAsync(ApplicationDbContext db, Location final, bool time, bool text, CancellationToken token)
    {
        if (db.Database.CurrentTransaction == null) throw new InvalidOperationException("History publication requires a transaction.");
        LocationHistoryDerivation.Apply(final, time, text);
        var query = db.Locations.Where(row => row.Id == final.Id);
        if (time)
            await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.CaptureLocalTimestamp, final.CaptureLocalTimestamp)
                .SetProperty(row => row.CaptureTimeZoneId, final.CaptureTimeZoneId)
                .SetProperty(row => row.CaptureOffsetSeconds, final.CaptureOffsetSeconds)
                .SetProperty(row => row.HistoryTimeVersion, final.HistoryTimeVersion)
                .SetProperty(row => row.HistoryTimeStatus, final.HistoryTimeStatus)
                .SetProperty(row => row.HistoryTimeFailureCode, final.HistoryTimeFailureCode), token);
        if (text)
            await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.HistorySearchText, final.HistorySearchText)
                .SetProperty(row => row.HistoryTextVersion, final.HistoryTextVersion), token);
    }
}
