using Quartz;
using Wayfarer.Models;

namespace Wayfarer.Jobs
{
    public class JobExecutionListener : IJobListener
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public JobExecutionListener(IServiceScopeFactory serviceScopeFactory)
        {
            _serviceScopeFactory = serviceScopeFactory;
        }

        public string Name => "JobExecutionListener";

        // Called before the job is executed
        public Task JobToBeExecuted(IJobExecutionContext context, CancellationToken cancellationToken)
        {
            // Optionally, you can perform pre-execution tasks here
            return Task.CompletedTask;
        }

        // Called if the job execution is vetoed
        public Task JobExecutionVetoed(IJobExecutionContext context, CancellationToken cancellationToken)
        {
            // Optionally, you can log or perform some tasks here
            return Task.CompletedTask;
        }

        // Called after the job is executed
        public async Task JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken)
        {
            // Create a new scope to resolve services
            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            // Resolve the scoped services here
            ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Extract job execution details
            string jobName = context.JobDetail.Key.Name;
            string jobGroup = context.JobDetail.Key.Group;
            string jobStatus = jobException == null ? "Completed" : "Failed";

            // Log the job execution status and other details to the database
            JobHistory jobHistory = new JobHistory
            {
                JobName = jobName,
                LastRunTime = DateTime.UtcNow,
                Status = jobStatus
            };

            // Add the job history to the database
            dbContext.JobHistories.Add(jobHistory);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
