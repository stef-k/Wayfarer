using System.Text.Json;
using Quartz;
using Wayfarer.Areas.Admin.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.DTOs;
using Wayfarer.Parsers;

namespace Wayfarer.Jobs
{
    /// <summary>
    /// Quartz job listener that logs job execution history and broadcasts SSE events.
    /// </summary>
    public class JobExecutionListener : IJobListener
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public JobExecutionListener(IServiceScopeFactory serviceScopeFactory)
        {
            _serviceScopeFactory = serviceScopeFactory;
        }

        public string Name => "JobExecutionListener";

        /// <summary>
        /// Called before the job is executed. Broadcasts job_started event.
        /// </summary>
        public async Task JobToBeExecuted(IJobExecutionContext context, CancellationToken cancellationToken)
        {
            await BroadcastJobStatusAsync("job_started", context, "Running", null);
        }

        /// <summary>
        /// Called if the job execution is vetoed.
        /// </summary>
        public Task JobExecutionVetoed(IJobExecutionContext context, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Called after the job is executed. Logs history and broadcasts completion event.
        /// </summary>
        public async Task JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken)
        {
            // Create a new scope to resolve services
            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Extract job execution details
            string jobName = context.JobDetail.Key.Name;
            string jobGroup = context.JobDetail.Key.Group;

            // Determine status from JobDataMap (jobs may set Cancelled status) or exception
            string jobStatus = context.JobDetail.JobDataMap["Status"]?.ToString() ?? "Completed";
            if (jobException != null)
            {
                jobStatus = "Failed";
            }

            // Log the job execution status to the database
            JobHistory jobHistory = new JobHistory
            {
                JobName = jobName,
                LastRunTime = DateTime.UtcNow,
                Status = jobStatus
            };

            dbContext.JobHistories.Add(jobHistory);
            await dbContext.SaveChangesAsync(cancellationToken);

            // Broadcast SSE event
            string eventType = jobStatus switch
            {
                "Failed" => "job_failed",
                "Cancelled" => "job_cancelled",
                _ => "job_completed"
            };

            await BroadcastJobStatusAsync(eventType, context, jobStatus, jobException?.Message);
        }

        /// <summary>
        /// Broadcasts a job status event to SSE subscribers.
        /// </summary>
        private async Task BroadcastJobStatusAsync(string eventType, IJobExecutionContext context, string status, string? errorMessage)
        {
            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            SseService sseService = scope.ServiceProvider.GetRequiredService<SseService>();

            var dto = new JobStatusSseEventDto
            {
                EventType = eventType,
                JobName = context.JobDetail.Key.Name,
                JobGroup = context.JobDetail.Key.Group,
                Status = status,
                TimestampUtc = DateTime.UtcNow,
                ErrorMessage = errorMessage
            };

            string json = JsonSerializer.Serialize(dto, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            await sseService.BroadcastAsync(JobsController.JobStatusChannel, json);
        }
    }
}
