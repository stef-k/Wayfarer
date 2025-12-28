using Quartz;

namespace Wayfarer.Jobs
{
    /// <summary>
    /// Quartz job that cleans up log files older than one month.
    /// Supports cancellation via CancellationToken.
    /// </summary>
    public class LogCleanupJob : IJob
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<LogCleanupJob> _logger;

        public LogCleanupJob(IConfiguration configuration, ILogger<LogCleanupJob> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public Task Execute(IJobExecutionContext context)
        {
            CancellationToken cancellationToken = context.CancellationToken;
            JobDataMap jobDataMap = context.JobDetail.JobDataMap;

            // Set the initial status to "Scheduled" when the job is first triggered
            jobDataMap["Status"] = "Scheduled";

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                _logger.LogInformation("LogCleanupJob started.");
                jobDataMap["Status"] = "In Progress";

                string? logDirectory = Path.GetDirectoryName(_configuration["Logging:LogFilePath:Default"]);

                if (string.IsNullOrEmpty(logDirectory))
                {
                    _logger.LogWarning("Log directory path could not be determined. Skipping log cleanup.");
                    jobDataMap["Status"] = "Completed";
                    return Task.CompletedTask;
                }

                string[] logFiles = Directory.GetFiles(logDirectory, "wayfarer-*.log");

                foreach (string logFile in logFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    FileInfo fileInfo = new FileInfo(logFile);
                    if (fileInfo.CreationTime < DateTime.Now.AddMonths(-1))
                    {
                        try
                        {
                            fileInfo.Delete();
                            _logger.LogInformation("Deleted old log file: {LogFile}", logFile);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to delete log file: {LogFile}\n{ex}", logFile, ex);
                        }
                    }
                }

                _logger.LogInformation("LogCleanupJob completed successfully.");
                jobDataMap["Status"] = "Completed";
            }
            catch (OperationCanceledException)
            {
                jobDataMap["Status"] = "Cancelled";
                _logger.LogInformation("LogCleanupJob was cancelled.");
            }
            catch (Exception ex)
            {
                jobDataMap["Status"] = "Failed";
                _logger.LogError(ex, "Error executing LogCleanupJob");
            }

            return Task.CompletedTask;
        }
    }
}
