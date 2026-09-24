using Quartz;
using Wayfarer.Services;
using System.Text.RegularExpressions;

namespace Wayfarer.Jobs
{
    /// <summary>
    /// Quartz job that cleans up log files older than one month.
    /// Supports cancellation via CancellationToken.
    /// </summary>
    public class LogCleanupJob : IJob
    {
        private readonly StoragePaths _storage;
        private readonly ILogger<LogCleanupJob> _logger;

        /// <summary>Uses the same operational directory as Serilog and Admin.</summary>
        public LogCleanupJob(StoragePaths storage, ILogger<LogCleanupJob> logger)
        {
            _storage = storage;
            _logger = logger;
        }

        /// <summary>Prunes only daily Wayfarer files while preserving cancellation and Quartz status.</summary>
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

                var logDirectory = _storage.LogRoot;
                var logFiles = Directory.Exists(logDirectory)
                    ? Directory.GetFiles(logDirectory, "wayfarer-*.log") : [];
                int deletedCount = 0;

                foreach (string logFile in logFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!Regex.IsMatch(Path.GetFileName(logFile), @"\Awayfarer-\d{8}\.log\z")) continue;
                    FileInfo fileInfo = new FileInfo(logFile);
                    if (fileInfo.CreationTime < DateTime.Now.AddMonths(-1))
                    {
                        try
                        {
                            fileInfo.Delete();
                            deletedCount++;
                            _logger.LogInformation("Deleted old log file: {LogFile}", logFile);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to delete log file: {LogFile}\n{ex}", logFile, ex);
                        }
                    }
                }

                _logger.LogInformation("LogCleanupJob completed successfully. Deleted {DeletedCount} files.", deletedCount);
                jobDataMap["Status"] = "Completed";
                jobDataMap["StatusMessage"] = $"Deleted {deletedCount} old log files";
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
