namespace Wayfarer.Models
{
    public class JobHistory
    {
        public int Id { get; set; }
        public string JobName { get; set; }
        public DateTime? LastRunTime { get; set; }
        public string Status { get; set; }
    }
}
