using Microsoft.EntityFrameworkCore;
using Quartz;
using Wayfarer.Models;

namespace Wayfarer.Jobs
{
    /// <summary>
    /// Quartz job that removes audit log entries older than two years.
    /// Supports cancellation via CancellationToken.
    /// </summary>
    public class AuditLogCleanupJob : IJob
    {
        private readonly ApplicationDbContext _dbContext;

        public AuditLogCleanupJob(ApplicationDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            CancellationToken cancellationToken = context.CancellationToken;

            cancellationToken.ThrowIfCancellationRequested();

            DateTime cutoffDate = DateTime.UtcNow.AddYears(-2);
            List<AuditLog> oldLogs = await _dbContext.AuditLogs
                .Where(log => log.Timestamp < cutoffDate)
                .ToListAsync(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            _dbContext.AuditLogs.RemoveRange(oldLogs);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
