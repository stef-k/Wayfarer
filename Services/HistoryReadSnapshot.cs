using Microsoft.EntityFrameworkCore.Storage;

namespace Wayfarer.Services;

/// <summary>Shared bounded cleanup for read-only history and private-calendar snapshots.</summary>
internal static class HistoryReadSnapshot
{
    /// <summary>A disconnect must not strand a snapshot or replace the primary query/projection failure.</summary>
    internal static async Task EndAsync(IDbContextTransaction transaction, bool failed)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Exception? error = null;
        try { await transaction.RollbackAsync(cleanup.Token).WaitAsync(cleanup.Token); }
        catch (Exception exception) { error = exception; }
        try { await transaction.DisposeAsync().AsTask().WaitAsync(cleanup.Token); }
        catch (Exception exception) { error ??= exception; }
        if (error != null && !failed) throw new InvalidOperationException("History read cleanup failed.");
    }

}
