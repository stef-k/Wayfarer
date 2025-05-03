namespace Wayfarer.Jobs;

using Quartz;
using Quartz.Spi;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

public class ScopedJobFactory : IJobFactory
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    // track scopes so we can dispose them in ReturnJob
    private readonly ConcurrentDictionary<IJob, IServiceScope> _scopes
        = new ConcurrentDictionary<IJob, IServiceScope>();

    public ScopedJobFactory(IServiceScopeFactory serviceScopeFactory)
    {
        _serviceScopeFactory = serviceScopeFactory;
    }

    public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler)
    {
        // 1) create a new DI scope
        var scope = _serviceScopeFactory.CreateScope();
        // 2) resolve the job type from that scope
        var job = (IJob)scope.ServiceProvider.GetRequiredService(bundle.JobDetail.JobType);
        // 3) remember the scope so we can dispose it later
        _scopes[job] = scope;
        return job;
    }

    public void ReturnJob(IJob job)
    {
        // dispose the scope we created for this job
        if (_scopes.TryRemove(job, out var scope))
        {
            scope.Dispose();
        }
    }
}
