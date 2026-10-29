using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Services.LocationEnrichment;

namespace Wayfarer.Services.LocationImports;

/// <summary>Retries the import's single pending fact and conditionally discharges its observed checkpoint.</summary>
public sealed class LocationImportEnrichmentHandoff(IDbContextFactory<ApplicationDbContext> contexts,
    IImportEnrichmentHandoff handoff, ILogger<LocationImportEnrichmentHandoff>? logger = null)
{
    /// <summary>Never holds an import context or lock while the shared workflow accepts the handoff.</summary>
    public async Task ReconcileAsync(int importId, CancellationToken token = default)
    {
        Checkpoint? observed;
        await using (var db = await contexts.CreateDbContextAsync(token))
            observed = await db.LocationImports.AsNoTracking()
                .Where(row => row.Id == importId && row.EnrichmentRequested && row.EnrichmentHandoffPending)
                .Select(row => new Checkpoint(row.UserId, row.LastProcessedIndex, row.ExecutionEpoch)).SingleOrDefaultAsync(token);
        if (observed is null) return;
        var disposition = await handoff.EnsureAsync(observed.UserId, token);
        if (disposition == EnrichmentHandoffDisposition.Pending) return;
        await using var clear = await contexts.CreateDbContextAsync(token);
        var current = clear.LocationImports.Where(row => row.Id == importId && row.UserId == observed.UserId
            && row.EnrichmentHandoffPending && row.LastProcessedIndex == observed.Index && row.ExecutionEpoch == observed.Epoch);
        if (clear.Database.IsRelational())
            await current.ExecuteUpdateAsync(set => set.SetProperty(row => row.EnrichmentHandoffPending, false), token);
        else
        {
            var row = await current.SingleOrDefaultAsync(token);
            if (row is null) return;
            row.EnrichmentHandoffPending = false;
            await clear.SaveChangesAsync(token);
        }
    }

    /// <summary>Visits one indexed page per maintenance tick; a rotating cursor avoids starvation by Running rows.</summary>
    public async Task<int> RetryPendingAsync(int afterId, CancellationToken token = default)
    {
        List<int> ids;
        await using (var db = await contexts.CreateDbContextAsync(token))
            ids = await db.LocationImports.AsNoTracking().Where(row => row.EnrichmentHandoffPending && row.Id > afterId)
                .OrderBy(row => row.Id).Select(row => row.Id).Take(100).ToListAsync(token);
        foreach (var id in ids)
        {
            try { await ReconcileAsync(id, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception)
            { logger?.LogWarning("Import enrichment handoff remains pending; import {ImportId}; code {Code}.", id, "enrichment-handoff-pending"); }
        }
        return ids.Count == 100 ? ids[^1] : 0;
    }

    private sealed record Checkpoint(string UserId, int Index, int Epoch);
}
