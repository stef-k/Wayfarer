using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Quartz;
using Quartz.Impl.Matchers;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;

namespace Wayfarer.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class JobsController : BaseController
    {
        private readonly IScheduler _scheduler;
        private readonly IServiceProvider _serviceProvider;

        public JobsController(IScheduler scheduler, IServiceProvider serviceProvider,
            ApplicationDbContext dbContext, ILogger<UsersController> logger) : base(logger, dbContext)
        {
            _scheduler = scheduler;
            _serviceProvider = serviceProvider;
        }

        /// <summary>
        /// Display all jobs
        /// </summary>
        /// <returns></returns>
        public async Task<IActionResult> Index()
        {
            IReadOnlyCollection<JobKey> jobKeys = await _scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());
            List<JobMonitoringViewModel> jobs = new List<JobMonitoringViewModel>();

            foreach (JobKey jobKey in jobKeys)
            {
                IJobDetail? jobDetail = await _scheduler.GetJobDetail(jobKey);
                IReadOnlyCollection<ITrigger> triggers = await _scheduler.GetTriggersOfJob(jobKey);

                DateTimeOffset? nextFireTime = triggers.FirstOrDefault()?.GetNextFireTimeUtc()?.ToLocalTime();
                DateTime? lastRunTime = await _dbContext.JobHistories
                    .Where(jh => jh.JobName == jobKey.Name)
                    .OrderByDescending(jh => jh.LastRunTime)
                    .Select(jh => jh.LastRunTime)
                    .FirstOrDefaultAsync();

                string status = lastRunTime.HasValue
                    ? jobDetail?.JobDataMap["Status"]?.ToString() ?? "In Progress"
                    : "Not Started";

                jobs.Add(new JobMonitoringViewModel
                {
                    JobName = jobKey.Name,
                    JobGroup = jobKey.Group,
                    NextFireTime = nextFireTime,
                    LastRunTime = lastRunTime?.ToLocalTime(),
                    Status = status
                });
            }
            SetPageTitle("Scheduled Jobs");
            return View("Index", jobs);
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




        private Task<string> GetJobStatusFromDb(string jobName)
        {
            // Retrieve the job status from the database (e.g., 'Failed', 'Pending', etc.)
            // Example: Return "Failed" if the job was previously marked as failed.
            return Task.FromResult("Failed"); // This is just an example, you can replace it with real logic
        }

    }

}
