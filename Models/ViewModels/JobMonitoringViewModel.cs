namespace Wayfarer.Models.ViewModels
{
    /// <summary>
    /// View model for displaying job status in the admin monitoring panel.
    /// </summary>
    public class JobMonitoringViewModel
    {
        /// <summary>The job's unique name.</summary>
        public string JobName { get; set; } = string.Empty;

        /// <summary>The group this job belongs to.</summary>
        public string JobGroup { get; set; } = string.Empty;

        /// <summary>When the job is next scheduled to fire.</summary>
        public DateTimeOffset? NextFireTime { get; set; }

        /// <summary>Current status text (Running, Paused, Scheduled, etc.).</summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>When the job last ran.</summary>
        public DateTimeOffset? LastRunTime { get; set; }

        /// <summary>Whether the job is currently executing.</summary>
        public bool IsRunning { get; set; }

        /// <summary>Whether the job's triggers are paused (won't fire until resumed).</summary>
        public bool IsPaused { get; set; }

        /// <summary>Whether the job supports cancellation via CancellationToken.</summary>
        public bool IsInterruptable { get; set; }
    }
}
