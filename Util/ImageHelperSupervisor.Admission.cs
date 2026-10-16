using System.Threading.RateLimiting;

namespace Wayfarer.Util;

/// <summary>Shares one size-weighted budget across disk reads and their complete validation lifetime.</summary>
public sealed partial class ImageHelperSupervisor
{
    // Eight small inputs or one 200 MiB input; rounding up also bounds child/thread count to eight.
    private const int ValidationUnitBytes = 25 * 1024 * 1024;
    private static readonly ConcurrencyLimiter ValidationAdmission = new(new ConcurrencyLimiterOptions
    {
        PermitLimit = 8,
        QueueLimit = 32,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
    });

    /// <summary>Finite pre-allocation wait; the bounded waiter list holds no encoded image buffers.</summary>
    internal static TimeSpan CacheAdmissionTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Reserve from actual file length before the first full read or large allocation.</summary>
    internal static async Task<RateLimitLease> AdmitCacheReadAsync(int length, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Volatile.Read(ref admissionDisabled) != 0) throw new ImageHelperUnavailableException();
        using var timeout = new CancellationTokenSource(CacheAdmissionTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        RateLimitLease lease;
        try { lease = await ValidationAdmission.AcquireAsync(ValidationUnits(length), linked.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ImageHelperUnavailableException();
        }
        if (lease.IsAcquired && Volatile.Read(ref admissionDisabled) == 0) return lease;
        lease.Dispose();
        throw new ImageHelperUnavailableException();
    }

    /// <summary>Small inputs consume one unit; 50/100/200 MiB consume two/four/eight units.</summary>
    private static int ValidationUnits(int length) => Math.Max(1, (length + ValidationUnitBytes - 1) / ValidationUnitBytes);

    /// <summary>Test observation of the actual shared limiter, including bounded waiting before allocation.</summary>
    internal static RateLimiterStatistics ValidationStatistics => ValidationAdmission.GetStatistics()!;

    /// <summary>Borrow a disk reader's lease; its caller retains ownership until validation and reap finish.</summary>
    internal Task<ImageTransformResult> ValidateAdmittedAsync(ReadOnlyMemory<byte> input,
        int maximumInputBytes, RateLimitLease admission, CancellationToken ct) =>
        RunAsync(input, new ImageTransform(MaximumInputBytes: maximumInputBytes),
            TimeSpan.FromSeconds(30), true, ct, admission);
}
