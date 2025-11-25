namespace Wayfarer.Models.ViewModels
{
    public class JobMonitoringViewModel
    {
        public string JobName { get; set; } = string.Empty;
        public string JobGroup { get; set; } = string.Empty;
        public DateTimeOffset? NextFireTime { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTimeOffset? LastRunTime { get; set; }
    }

}
