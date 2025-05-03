using Quartz;

namespace Wayfarer.Services // Namespace
{
    public class QuartzHostedService : IHostedService
    {
        private readonly IScheduler _scheduler;

        // Constructor to inject the Quartz scheduler
        public QuartzHostedService(IScheduler scheduler)
        {
            _scheduler = scheduler;
        }

        // Called when the app starts (on Start)
        public async Task StartAsync(CancellationToken cancellationToken)
        {
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
