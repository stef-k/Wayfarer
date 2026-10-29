namespace Wayfarer.Services.LocationImports;

/// <summary>Retries import-specific projection/custody work at startup and every five minutes, without GET side effects.</summary>
public sealed class ManagedImportMaintenance(IServiceScopeFactory scopes, ILogger<ManagedImportMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<LocationImportReconciler>().ReconcileAsync(stoppingToken, managedOnly: true);
                await scope.ServiceProvider.GetRequiredService<ManagedImportCleanup>().MaintainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception)
            {
                logger.LogWarning("Import maintenance remains pending; code {Code}.", "import-maintenance-unavailable");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
