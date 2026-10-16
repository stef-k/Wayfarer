using Wayfarer.Services;
using Wayfarer.Tests.Util;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Exercise admission at the real disk reader with small ordinary files.</summary>
public partial class ProxiedImageCacheServiceTests
{
    /// <summary>A present cache file waits before reading when all validation capacity is active.</summary>
    [Fact]
    public async Task CachedRead_WaitsForValidationAdmissionAndHonorsCancellation()
    {
        var service = CreateService();
        var key = Key("admission");
        Assert.True((await service.SetAsync(key, [1, 2, 3], "image/png")).Stored);
        using var stopChildren = new CancellationTokenSource();
        var helpers = Enumerable.Range(0, 8).Select(_ => new FaultHelper("time.sleep(60)")).ToArray();
        var active = helpers.Select(helper => helper.Supervisor.ValidateAsync(
            ReadOnlyMemory<byte>.Empty, cancellationToken: stopChildren.Token)).ToArray();
        try
        {
            await Task.WhenAll(helpers.Select(helper => helper.WaitForPid()));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAsync(key, cancellation.Token));
        }
        finally
        {
            stopChildren.Cancel();
            foreach (var task in active)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            foreach (var helper in helpers)
            {
                ImageHelperSupervisorTests.AssertReaped(await helper.WaitForPid());
                helper.Dispose();
            }
        }
    }
}
