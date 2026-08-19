using Wayfarer.Models;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Orders pacing, concurrency, final authority, rate admission, and monotonic attempt start.</summary>
public sealed class RoutingAttemptCoordinator
{
    private readonly RoutingProviderPacer _pacer;
    private readonly RoutingRequestBudget _budget;

    /// <summary>Initializes the narrow provider-attempt admission coordinator.</summary>
    public RoutingAttemptCoordinator(RoutingProviderPacer pacer, RoutingRequestBudget budget)
        => (_pacer, _budget) = (pacer, budget);

    /// <summary>Prepares one actual provider attempt immediately before DNS resolution.</summary>
    public async Task<RoutingAttemptAdmission> PrepareAsync(
        RoutingProviderConfiguration provider, Func<CancellationToken, Task<bool>> validateAuthority,
        CancellationToken cancellationToken)
    {
        _pacer.ApplyConfiguration(provider.Id, provider.ConfigurationVersion, provider.MinimumIntervalMilliseconds);
        var paced = await _pacer.WaitAsync(provider.Id, provider.ConfigurationVersion, cancellationToken);
        if (!paced.Succeeded) return RoutingAttemptAdmission.Failure(paced.ErrorCode!);
        var turn = paced.Turn!;
        var concurrency = await _budget.AcquireAttemptConcurrencyAsync(
            provider.Id, provider.MaxConcurrency, cancellationToken);
        if (concurrency == null)
        {
            turn.Dispose();
            return RoutingAttemptAdmission.Failure("routing-rate-limited");
        }
        try
        {
            if (!await validateAuthority(cancellationToken))
            {
                concurrency.Dispose();
                return RoutingAttemptAdmission.Failure("provider-configuration-stale");
            }
            return RoutingAttemptAdmission.Prepared(concurrency, turn,
                () => _budget.TryAdmitProviderAttempt(provider.Id, provider.RequestsPerMinute));
        }
        catch
        {
            turn.Dispose();
            concurrency.Dispose();
            throw;
        }
    }
}
