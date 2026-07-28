namespace Wayfarer.Models;

/// <summary>
/// Defines only the approved migration/initialization records; runtime choices are always queried from the database.
/// </summary>
public static class TransportProfileSeedData
{
    /// <summary>Gets the approved starter records with stable identities and ordering.</summary>
    public static IReadOnlyList<TransportProfile> Create() =>
    [
        Profile("11111111-0000-0000-0000-000000000001", "walk", "Walk", "Active", 5, 10),
        Profile("11111111-0000-0000-0000-000000000002", "bicycle", "Bicycle", "Active", 15, 20),
        Profile("11111111-0000-0000-0000-000000000003", "bike", "Motorcycle", "Road", 40, 30),
        Profile("11111111-0000-0000-0000-000000000004", "car", "Car", "Road", 60, 40),
        Profile("11111111-0000-0000-0000-000000000005", "bus", "Bus / coach", "Road", 35, 50),
        Profile("11111111-0000-0000-0000-000000000006", "tram", "Tram / streetcar", "Urban rail", 20, 60),
        Profile("11111111-0000-0000-0000-000000000007", "metro", "Metro / subway", "Urban rail", 35, 70),
        Profile("11111111-0000-0000-0000-000000000008", "regional-train", "Regional train", "Rail", 70, 80),
        Profile("11111111-0000-0000-0000-000000000009", "train", "Train (general)", "Rail", 100, 90),
        Profile("11111111-0000-0000-0000-000000000010", "intercity-train", "Intercity train", "Rail", 120, 100),
        Profile("11111111-0000-0000-0000-000000000011", "high-speed-train", "High-speed train", "Rail", 250, 110),
        Profile("11111111-0000-0000-0000-000000000012", "ferry", "Ferry", "Water", 30, 120),
        Profile("11111111-0000-0000-0000-000000000013", "boat", "Boat", "Water", 25, 130),
        Profile("11111111-0000-0000-0000-000000000014", "flight", "Flight", "Air", 800, 140),
        Profile("11111111-0000-0000-0000-000000000015", "helicopter", "Helicopter", "Air", 200, 150)
    ];

    private static TransportProfile Profile(string id, string key, string label, string category, double speed, int sortOrder) => new()
    {
        Id = Guid.Parse(id),
        Key = key,
        Label = label,
        Category = category,
        PlanningSpeedKmh = speed,
        SortOrder = sortOrder,
        IsActive = true,
        IsSeeded = true,
        Description = "Average planning assumption; use a manual duration when the service differs."
    };
}
