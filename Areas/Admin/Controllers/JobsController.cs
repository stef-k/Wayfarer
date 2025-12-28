using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Quartz;
using Quartz.Impl.Matchers;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Parsers;

namespace Wayfarer.Areas.Admin.Controllers
{
    /// <summary>
    /// Admin controller for monitoring and controlling scheduled Quartz jobs.
    /// </summary>
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    [Route("Admin/[controller]/[action]")]
    public class JobsController : BaseController
    {
        /// <summary>
        /// SSE channel name for job status updates.
        /// </summary>
        public const string JobStatusChannel = "admin-job-status";

        private readonly IScheduler _scheduler;
        private readonly IServiceProvider _serviceProvider;
        private readonly SseService _sseService;

        public JobsController(IScheduler scheduler, IServiceProvider serviceProvider,
            ApplicationDbContext dbContext, ILogger<UsersController> logger, SseService sseService) : base(logger, dbContext)
        {
            _scheduler = scheduler;
            _serviceProvider = serviceProvider;
            _sseService = sseService;
        }

        /// <summary>
        /// Display all jobs with their current state (running, paused, scheduled).
        /// </summary>
        public async Task<IActionResult> Index()
        {
            IReadOnlyCollection<JobKey> jobKeys = await _scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());
            IReadOnlyCollection<IJobExecutionContext> currentlyExecuting = await _scheduler.GetCurrentlyExecutingJobs();
            HashSet<JobKey> runningJobKeys = currentlyExecuting.Select(c => c.JobDetail.Key).ToHashSet();

            List<JobMonitoringViewModel> jobs = new List<JobMonitoringViewModel>();

            foreach (JobKey jobKey in jobKeys)
            {
                IJobDetail? jobDetail = await _scheduler.GetJobDetail(jobKey);
                IReadOnlyCollection<ITrigger> triggers = await _scheduler.GetTriggersOfJob(jobKey);
                ITrigger? trigger = triggers.FirstOrDefault();

                // Check if paused by examining trigger state
                bool isPaused = false;
                if (trigger != null)
                {
                    TriggerState triggerState = await _scheduler.GetTriggerState(trigger.Key);
                    isPaused = triggerState == TriggerState.Paused;
                }

                // Check if currently running
                bool isRunning = runningJobKeys.Contains(jobKey);

                // All our jobs support CancellationToken pattern
                bool isInterruptable = true;

                DateTimeOffset? nextFireTime = trigger?.GetNextFireTimeUtc()?.ToLocalTime();
                DateTime? lastRunTime = await _dbContext.JobHistories
                    .Where(jh => jh.JobName == jobKey.Name)
                    .OrderByDescending(jh => jh.LastRunTime)
                    .Select(jh => jh.LastRunTime)
                    .FirstOrDefaultAsync();

                string status = DetermineStatus(isRunning, isPaused, jobDetail, lastRunTime);
                string? statusMessage = jobDetail?.JobDataMap["StatusMessage"]?.ToString();

                jobs.Add(new JobMonitoringViewModel
                {
                    JobName = jobKey.Name,
                    JobGroup = jobKey.Group,
                    NextFireTime = nextFireTime,
                    LastRunTime = lastRunTime?.ToLocalTime(),
                    Status = status,
                    StatusMessage = statusMessage,
                    IsRunning = isRunning,
                    IsPaused = isPaused,
                    IsInterruptable = isInterruptable
                });
            }
            SetPageTitle("Scheduled Jobs");
            return View("Index", jobs);
        }

        /// <summary>
        /// Determines the display status based on job state.
        /// </summary>
        private static string DetermineStatus(bool isRunning, bool isPaused, IJobDetail? jobDetail, DateTime? lastRunTime)
        {
            if (isRunning) return "Running";
            if (isPaused) return "Paused";
            if (!lastRunTime.HasValue) return "Not Started";
            return jobDetail?.JobDataMap["Status"]?.ToString() ?? "Scheduled";
        }


        /// <summary>
        /// Start or retry a job
        /// </summary>
        /// <param name="jobName">Job Name</param>
        /// <param name="jobGroup">Job Group</param>
        /// <returns></returns>
        [HttpPost]
        public async Task<IActionResult> StartJob(string jobName, string jobGroup)
        {
            JobKey jobKey = new JobKey(jobName, jobGroup);

            // Get the current time for LastRunTime
            DateTime currentTime = DateTime.UtcNow;

            // Find the existing JobHistory record or create a new one
            using (ApplicationDbContext dbContext = _serviceProvider.GetRequiredService<ApplicationDbContext>())
            {
                JobHistory? existingJobHistory = await dbContext.JobHistories
                                                         .FirstOrDefaultAsync(j => j.JobName == jobName);

                if (existingJobHistory != null)
                {
                    // Update the existing job history record
                    existingJobHistory.LastRunTime = currentTime;
                    existingJobHistory.Status = "In Progress"; // Optional: Set status to "In Progress"
                }
                else
                {
                    // Add a new job history record if not found
                    dbContext.JobHistories.Add(new JobHistory
                    {
                        JobName = jobName,
                        LastRunTime = currentTime,
                        Status = "In Progress"
                    });
                }

                await dbContext.SaveChangesAsync();
            }

            // Trigger the job immediately
            IJobDetail? jobDetail = await _scheduler.GetJobDetail(jobKey);

            if (jobDetail != null)
            {
                // Create a trigger to run the job immediately
                ITrigger trigger = TriggerBuilder.Create()
                                            .WithIdentity($"{jobName}-manual-trigger", jobGroup)
                                            .StartNow()  // Run immediately
                                            .Build();

                // Trigger the existing job
                await _scheduler.TriggerJob(jobKey, new JobDataMap()); // Trigger the job without creating a new instance
            }

            TempData["Message"] = "Job started.";

            return RedirectToAction("Index");
        }

        /// <summary>
        /// Pause a job's scheduled execution. The job won't fire while paused.
        /// If the job is currently running, it will continue until completion.
        /// </summary>
        /// <param name="jobName">Job name</param>
        /// <param name="jobGroup">Job group</param>
        [HttpPost]
        public async Task<IActionResult> PauseJob(string jobName, string jobGroup)
        {
            JobKey jobKey = new JobKey(jobName, jobGroup);
            await _scheduler.PauseJob(jobKey);

            _logger.LogInformation("Admin paused job {JobName} in group {JobGroup}", jobName, jobGroup);
            TempData["Message"] = $"Job '{jobName}' paused. It will not run until resumed.";
            return RedirectToAction("Index");
        }

        /// <summary>
        /// Resume a paused job, allowing it to fire on its schedule again.
        /// </summary>
        /// <param name="jobName">Job name</param>
        /// <param name="jobGroup">Job group</param>
        [HttpPost]
        public async Task<IActionResult> ResumeJob(string jobName, string jobGroup)
        {
            JobKey jobKey = new JobKey(jobName, jobGroup);
            await _scheduler.ResumeJob(jobKey);

            _logger.LogInformation("Admin resumed job {JobName} in group {JobGroup}", jobName, jobGroup);
            TempData["Message"] = $"Job '{jobName}' resumed.";
            return RedirectToAction("Index");
        }

        /// <summary>
        /// Request cancellation of a currently running job.
        /// The job must cooperate by checking CancellationToken.
        /// </summary>
        /// <param name="jobName">Job name</param>
        /// <param name="jobGroup">Job group</param>
        [HttpPost]
        public async Task<IActionResult> CancelJob(string jobName, string jobGroup)
        {
            JobKey jobKey = new JobKey(jobName, jobGroup);
            bool interrupted = await _scheduler.Interrupt(jobKey);

            if (interrupted)
            {
                _logger.LogInformation("Admin requested cancellation of job {JobName} in group {JobGroup}", jobName, jobGroup);
                TempData["Message"] = $"Job '{jobName}' cancellation requested.";
            }
            else
            {
                _logger.LogWarning("Could not interrupt job {JobName} in group {JobGroup} - not running or not interruptable", jobName, jobGroup);
                TempData["Error"] = $"Job '{jobName}' could not be cancelled (not running or not interruptable).";
            }

            return RedirectToAction("Index");
        }

        /// <summary>
        /// SSE endpoint for real-time job status updates.
        /// Broadcasts job_started, job_completed, job_failed, job_cancelled events.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>SSE stream with job status events.</returns>
        [HttpGet]
        public async Task<IActionResult> Sse(CancellationToken cancellationToken)
        {
            await _sseService.SubscribeAsync(
                JobStatusChannel,
                Response,
                cancellationToken,
                enableHeartbeat: true,
                heartbeatInterval: TimeSpan.FromSeconds(30));
            return new EmptyResult();
        }
    }
}
