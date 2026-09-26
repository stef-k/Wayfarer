namespace Wayfarer.Models.Options;

/// <summary>Finite process-local SSE resource policy; configured under Sse.</summary>
public sealed class SseOptions
{
    /// <summary>Maximum admitted streams across every endpoint and identity.</summary>
    public int MaxConnections { get; set; } = 2048;
    /// <summary>Aggregate stream budget for a resolved authenticated user.</summary>
    public int MaxConnectionsPerUser { get; set; } = 256;
    /// <summary>Anonymous stream budget per middleware-resolved effective IP.</summary>
    public int MaxConnectionsPerAnonymousIp { get; set; } = 128;
    /// <summary>Total slot, lease, write and flush deadline for one attempt.</summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Maximum recipient workers in one broadcast.</summary>
    public int FanoutConcurrency { get; set; } = 32;
    /// <summary>Total broadcast horizon, including all worker waves.</summary>
    public TimeSpan BroadcastTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Rejects invalid bounds at startup rather than silently weakening policy.</summary>
    public void Validate()
    {
        if (MaxConnections <= 0 || MaxConnectionsPerUser <= 0 || MaxConnectionsPerUser > MaxConnections
            || MaxConnectionsPerAnonymousIp <= 0 || MaxConnectionsPerAnonymousIp > MaxConnections
            || FanoutConcurrency <= 0 || !IsFiniteTimeout(SendTimeout) || !IsFiniteTimeout(BroadcastTimeout))
            throw new ArgumentOutOfRangeException(nameof(SseOptions), "SSE limits must be positive; identity limits must fit the global ceiling and timeouts must fit a finite timer.");
    }

    private static bool IsFiniteTimeout(TimeSpan value) => value > TimeSpan.Zero
        && value.TotalMilliseconds <= uint.MaxValue - 1;
}
