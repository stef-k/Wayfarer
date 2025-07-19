namespace Wayfarer.Models.Dtos;

public class ActivityTypeDto
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
}