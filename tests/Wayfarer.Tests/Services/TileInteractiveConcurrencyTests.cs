using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Contains the issue #396 provider-wide Interactive concurrency regression.</summary>
public sealed partial class TileProviderStateAdmissionTests
{
    /// <summary>Interactive permits the scheduler's full two-client foreground width.</summary>
    [Fact]
    public async Task Interactive_TwoClientsStartTwelveContacts_WithSixPerClient()
    {
        TileCacheService.OutboundBudget.ResetForTesting();
        TileWorkScheduler.ResetForTesting();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var startedCount = 0;
        var profile = TileProviderPolicyResolver.Resolve(new ApplicationSettings());

        async Task<TileRetrievalResult> ContactAsync(string clientKey, CancellationToken token)
        {
            var acquisition = await TileCacheService.OutboundBudget.AcquireProviderContactAsync(
                profile, TileWorkPriority.Foreground, token);
            using var lease = Assert.IsType<TileCacheService.OutboundBudget.ProviderContactLease>(acquisition.Lease);
            lock (counts)
            {
                counts[clientKey] = counts.GetValueOrDefault(clientKey) + 1;
                if (++startedCount == TileWorkScheduler.ForegroundConcurrency)
                    started.TrySetResult();
            }
            await release.Task.WaitAsync(token);
            return TileRetrievalResult.Success([1]);
        }

        try
        {
            var work = new[] { "client-a", "client-b" }
                .SelectMany(client => Enumerable.Range(0, TileWorkScheduler.PerClientConcurrency)
                    .Select(index => TileWorkScheduler.ExecuteForegroundAsync(
                        $"{client}:{index}", client,
                        token => ContactAsync(client, token), CancellationToken.None)))
                .ToArray();

            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(12, startedCount);
            Assert.Equal(6, counts["client-a"]);
            Assert.Equal(6, counts["client-b"]);
            Assert.Equal(0, TileCacheService.OutboundBudget.ProviderReplenisherStartCountForTesting);
            release.TrySetResult();
            await Task.WhenAll(work);
        }
        finally
        {
            release.TrySetResult();
            TileWorkScheduler.ResetForTesting();
            TileCacheService.OutboundBudget.ResetForTesting();
        }
    }
}
