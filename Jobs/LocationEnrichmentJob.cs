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
        var workflow = await db.LocationEnrichmentWorkflows.SingleOrDefaultAsync(item => item.SchedulerId == id,
            context.CancellationToken);
        if (workflow is null || !workflow.TryClaim(epoch, DateTime.UtcNow)) return;
        await db.SaveChangesAsync(context.CancellationToken);
        await worker.RunBatchAsync(workflow.UserId, epoch, context.CancellationToken);
    }
}

/// <summary>Runs at most one independent enrichment batch for a claimed user epoch.</summary>
public interface ILocationEnrichmentWorker
{
    Task RunBatchAsync(string userId, int epoch, CancellationToken cancellationToken);
}
