using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;

namespace Wayfarer.Services.LocationEnrichment;

/// <summary>Turns committed import opt-in into the user's single durable workflow intent.</summary>
public interface IImportEnrichmentHandoff
{
    Task EnsureAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>Creates or resumes relational intent before projecting one Quartz trigger.</summary>
public sealed class ImportEnrichmentHandoff(
    ApplicationDbContext db, IWorkflowScheduleProjection projection) : IImportEnrichmentHandoff
{
    public async Task EnsureAsync(string userId, CancellationToken cancellationToken = default)
    {
        var workflow = await db.LocationEnrichmentWorkflows.SingleOrDefaultAsync(
            item => item.UserId == userId, cancellationToken);
        if (workflow is null)
        {
            workflow = LocationEnrichmentWorkflow.Create(userId, DateTime.UtcNow);
            db.Add(workflow);
        }
        workflow.Start(DateTime.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        await projection.ProjectAsync(userId, cancellationToken);
    }
}
