using Quartz;
using Wayfarer.Models;

public class AuditLogCleanupJob : IJob
{
    private readonly ApplicationDbContext _dbContext;

    public AuditLogCleanupJob(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        DateTime cutoffDate = DateTime.UtcNow.AddYears(-2); // Adjust retention period as needed
        IQueryable<AuditLog> oldLogs = _dbContext.AuditLogs.Where(log => log.Timestamp < cutoffDate);

        _dbContext.AuditLogs.RemoveRange(oldLogs);
        await _dbContext.SaveChangesAsync();
    }
}
