using System;

namespace Wayfarer.Models.Options;

/// <summary>
/// Configuration for mobile SSE subscriptions.
/// </summary>
public class MobileSseOptions
{
    /// <summary>
    /// Heartbeat interval in milliseconds (defaults to 20 seconds).
    /// </summary>
    public int HeartbeatIntervalMilliseconds { get; set; } = 20_000;

    /// <summary>
    /// Returns the configured interval as a TimeSpan, clamping to at least 1 millisecond.
    /// </summary>
    public TimeSpan HeartbeatInterval => TimeSpan.FromMilliseconds(Math.Max(1, HeartbeatIntervalMilliseconds));
}
