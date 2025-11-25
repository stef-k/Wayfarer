namespace Wayfarer.Models.ViewModels
{
    public class DeleteLocationViewModel
    {
        public int Id { get; set; }
        public string LocationName { get; set; } = string.Empty; // Can be the address or any identifier
        public string? ActivityType { get; set; }
        public string? Notes { get; set; }
        public DateTime LocalTimestamp { get; set; }
        public string? Address { get; set; }
    }

}
