namespace Wayfarer.Models.ViewModels
{
    public class JobMonitoringViewModel
    {
        public string JobName { get; set; }
        public string JobGroup { get; set; }
        public DateTimeOffset? NextFireTime { get; set; }
        public string Status { get; set; }
        public DateTimeOffset? LastRunTime { get; set; }
    }

}
