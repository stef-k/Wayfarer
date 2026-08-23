using Quartz;

namespace Wayfarer.Parsers // Namespace
{
    public class QuartzHostedService : IHostedService
    {
        private readonly IScheduler _scheduler;
        private readonly IServiceScopeFactory? _scopeFactory;

        // Constructor to inject the Quartz scheduler
        public QuartzHostedService(IScheduler scheduler, IServiceScopeFactory? scopeFactory = null)
        {
            _scheduler = scheduler;
            _scopeFactory = scopeFactory;
        }

        // Called when the app starts (on Start)
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_scopeFactory is not null)
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider
                    .GetRequiredService<Wayfarer.Services.LocationEnrichment.LocationEnrichmentReconciler>()
                    .ReconcileAsync(cancellationToken);
            }
            // Start the Quartz scheduler when the app starts
            await _scheduler.Start(cancellationToken);
        }

        // Called when the app shuts down (on Stop)
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            // Gracefully shut down Quartz and wait for jobs to complete before stopping
            await _scheduler.Shutdown(waitForJobsToComplete: true, cancellationToken);
        }
    }
}
