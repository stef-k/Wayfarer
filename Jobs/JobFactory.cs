using Quartz;
using Quartz.Spi;

namespace Wayfarer.Jobs
{
    public class JobFactory : IJobFactory
    {
        private readonly IServiceProvider _serviceProvider;

        public JobFactory(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler)
        {
            Type jobType = bundle.JobDetail.JobType;
            return (IJob)_serviceProvider.GetRequiredService(jobType);
        }

        public void ReturnJob(IJob job)
        {
            // No-op. We don't need to return jobs to a pool.
        }
    }
}
