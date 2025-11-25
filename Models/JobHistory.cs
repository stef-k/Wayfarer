namespace Wayfarer.Models
{
    public class JobHistory
    {
        public int Id { get; set; }
        public required string JobName { get; set; }
        public DateTime? LastRunTime { get; set; }
        public required string Status { get; set; }
    }
}
