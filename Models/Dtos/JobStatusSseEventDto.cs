namespace Wayfarer.Models.DTOs;

/// <summary>
/// DTO for job status SSE events broadcast to admin clients.
/// </summary>
public class JobStatusSseEventDto
{
    /// <summary>
    /// Type of event: "job_started", "job_completed", "job_failed", "job_cancelled".
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// The job's unique name.
    /// </summary>
    public string JobName { get; set; } = string.Empty;

    /// <summary>
    /// The group this job belongs to.
    /// </summary>
    public string JobGroup { get; set; } = string.Empty;

    /// <summary>
    /// Current status: Running, Completed, Failed, Cancelled, Paused, Scheduled.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp of the event.
    /// </summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>
    /// Error message if job failed, null otherwise.
    /// </summary>
    public string? ErrorMessage { get; set; }
}
