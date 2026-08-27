using Microsoft.EntityFrameworkCore;
using Quartz;
using Wayfarer.Models;
using Wayfarer.Services.LocationEnrichment;

namespace Wayfarer.Jobs;

/// <summary>Validates relational epoch and intent before entering one bounded worker execution.</summary>
[DisallowConcurrentExecution]
public sealed class LocationEnrichmentJob(ApplicationDbContext db, ILocationEnrichmentWorker worker) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var data = context.MergedJobDataMap;
        if (data.GetInt("schema") != 1 || !Guid.TryParseExact(data.GetString("workflowId"), "N", out var id)) return;
        var epoch = data.GetInt("epoch");
        var userId = await db.LocationEnrichmentWorkflows.AsNoTracking()
            .Where(item => item.SchedulerId == id && item.Epoch == epoch && item.IntentEnabled)
            .Select(item => item.UserId).SingleOrDefaultAsync(context.CancellationToken);
        if (userId is null) return;
        _ = await worker.RunBatchAsync(userId, epoch, context.CancellationToken);
    }
}

/// <summary>Runs at most one independent enrichment batch for a claimed user epoch.</summary>
public interface ILocationEnrichmentWorker
{
    Task<LocationEnrichmentWorkerOutcome> RunBatchAsync(
        string userId, int epoch, CancellationToken cancellationToken);
}

/// <summary>Classifies one bounded firing without exposing provider or persistence content.</summary>
public enum LocationEnrichmentWorkerOutcome { Completed, AuthorityUnavailable, StaleOwner, Cancelled }
