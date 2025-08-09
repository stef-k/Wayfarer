using Microsoft.AspNetCore.Identity;
using Wayfarer.Models;

public class ApplicationDbContextSeed
{
    public static async Task SeedAsync(UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager,
        IServiceProvider serviceProvider)
    {
        // Seed application settings
        await SeedApplicationSettingsAsync(serviceProvider);

        // Seed roles
        await SeedRolesAsync(roleManager);

        // Seed Admin user
        await SeedAdminUserAsync(userManager);

        // Seed default Activity Types
        await SeedActivityTypes(serviceProvider);
    }

    /// <summary>
    /// Seed application's user Roles: Admin, Manager, User and Vehicle
    /// </summary>
    /// <param name="roleManager"></param>
    /// <returns></returns>
    private static async Task SeedRolesAsync(RoleManager<IdentityRole> roleManager)
    {
        // Create roles if they don't exist
        string[] roleNames = new[] { "Admin", "Manager", "User", "Vehicle" };

        foreach (string? roleName in roleNames)
        {
            bool roleExists = await roleManager.RoleExistsAsync(roleName);
            if (!roleExists)
            {
                IdentityRole role = new IdentityRole(roleName);
                await roleManager.CreateAsync(role);
            }
        }
    }

    /// <summary>
    /// Seed application's default Admin user
    /// </summary>
    /// <param name="userManager"></param>
    /// <returns></returns>
    private static async Task SeedAdminUserAsync(UserManager<ApplicationUser> userManager)
    {
        // Check if the Admin user exists, and create it if not
        ApplicationUser? adminUser = await userManager.FindByNameAsync("admin");

        if (adminUser == null)
        {
            adminUser = new ApplicationUser
            {
                UserName = "admin",
                DisplayName = "Wayfarer Administrator",
                IsActive = true, // Ensure the admin is active
                IsProtected = true // Ensure the admin is protected and cannot be deleted
            };

            IdentityResult createResult = await userManager.CreateAsync(adminUser, "Admin1!");

            if (createResult.Succeeded)
            {
                // Assign the Admin role to the new user
                await userManager.AddToRoleAsync(adminUser, "Admin");
            }
        }
    }

    /// <summary>
    /// Seed application's default settings
    /// </summary>
    /// <param name="serviceProvider"></param>
    /// <returns></returns>
    private static async Task SeedApplicationSettingsAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await dbContext.Database.EnsureCreatedAsync();

        var settings = await dbContext.ApplicationSettings.FindAsync(1);

        if (settings == null)
        {
            dbContext.ApplicationSettings.Add(new ApplicationSettings
            {
                LocationTimeThresholdMinutes = 5,
                LocationDistanceThresholdMeters = 15,
                MaxCacheTileSizeInMB = ApplicationSettings.DefaultMaxCacheTileSizeInMB,
                UploadSizeLimitMB = ApplicationSettings.DefaultUploadSizeLimitMB,
                IsRegistrationOpen = false
            });
        }
        else
        {
            bool changed = false;

            // Thresholds (0 = invalid, should always be > 0)
            if (settings.LocationTimeThresholdMinutes <= 0)
            {
                settings.LocationTimeThresholdMinutes = 5;
                changed = true;
            }

            if (settings.LocationDistanceThresholdMeters <= 0)
            {
                settings.LocationDistanceThresholdMeters = 15;
                changed = true;
            }

            // Cache/Upload sizes (0 = not set, use fallback)
            if (settings.MaxCacheTileSizeInMB == 0)
            {
                settings.MaxCacheTileSizeInMB = ApplicationSettings.DefaultMaxCacheTileSizeInMB;
                changed = true;
            }
            if (settings.UploadSizeLimitMB == 0)
            {
                settings.UploadSizeLimitMB = ApplicationSettings.DefaultUploadSizeLimitMB;
                changed = true;
            }

            if (changed)
            {
                dbContext.ApplicationSettings.Update(settings);
            }
        }

        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Add default activities in DB
    /// </summary>
    /// <param name="dbContext"></param>
    /// <returns></returns>
    private static async Task SeedActivityTypes(IServiceProvider serviceProvider)
    {
        using IServiceScope scope = serviceProvider.CreateScope();
        ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Check if ActivityTypes are already seeded
        if (!dbContext.ActivityTypes.Any())
        {
            dbContext.ActivityTypes.AddRange(
                // Travel & Commute Activities
                new ActivityType
                {
                    Name = "Commuting", Description = "Traveling to and from regular destinations like work or home."
                },
                new ActivityType { Name = "Flight", Description = "Traveling by airplane to a destination." },
                new ActivityType
                    { Name = "Train Journey", Description = "Traveling by train between cities or locations." },
                new ActivityType { Name = "Bus Ride", Description = "Taking a bus to a specific destination." },
                new ActivityType
                {
                    Name = "Road Trip",
                    Description = "Driving between different locations, usually over a longer distance."
                },
                new ActivityType
                {
                    Name = "Local Transport", Description = "Using public transport such as buses or subways in a city."
                },
                new ActivityType
                {
                    Name = "Motorcycle Ride",
                    Description = "Traveling on a motorcycle, either for commuting or leisure."
                },
                new ActivityType
                {
                    Name = "Taxi Ride",
                    Description = "Taking a taxi or ride-sharing service to travel between locations."
                },
                new ActivityType
                    { Name = "Carpool", Description = "Sharing a car ride with others to travel to a destination." },
                new ActivityType
                {
                    Name = "Limousine Ride",
                    Description = "Traveling in a limousine, often for special occasions or luxury."
                },
                new ActivityType
                {
                    Name = "Uber/Lyft Ride",
                    Description = "Using a ride-sharing app like Uber or Lyft to travel between locations."
                },

                // Leisure & Social Activities
                new ActivityType
                    { Name = "Sightseeing", Description = "Visiting tourist spots, landmarks, or attractions." },
                new ActivityType
                {
                    Name = "Vacation",
                    Description = "Traveling to a leisure destination, such as a resort, beach, or mountain location."
                },
                new ActivityType
                {
                    Name = "Nature Activity",
                    Description =
                        "Engaging in outdoor activities like hiking, camping, or fishing in natural surroundings."
                },
                new ActivityType
                {
                    Name = "Social Event",
                    Description = "Attending social gatherings like parties, concerts, or meetups."
                },
                new ActivityType
                {
                    Name = "Family Time",
                    Description = "Spending time with family members at home or at other locations."
                },
                new ActivityType
                {
                    Name = "Friend Meeting", Description = "Meeting friends at a café, park, or other gathering spot."
                },
                new ActivityType
                {
                    Name = "Celebration",
                    Description = "Celebrating a special event like a birthday, anniversary, or holiday at a location."
                },
                new ActivityType
                {
                    Name = "Community Gathering",
                    Description =
                        "Attending or participating in community events like fairs, markets, or public gatherings."
                },
                new ActivityType
                {
                    Name = "Festival",
                    Description = "Attending religious, cultural, or local festivals at specific venues."
                },
                new ActivityType
                {
                    Name = "Networking",
                    Description = "Engaging in professional networking activities at events or locations."
                },
                new ActivityType
                {
                    Name = "Club Night",
                    Description = "Going to a nightclub or bar to socialize and enjoy drinks and entertainment."
                },
                new ActivityType
                {
                    Name = "Bar Visit",
                    Description = "Visiting a bar for drinks and socializing with friends or acquaintances."
                },
                new ActivityType
                {
                    Name = "Happy Hour",
                    Description =
                        "Attending a happy hour at a bar, pub, or restaurant to enjoy discounted drinks and socialize."
                },
                new ActivityType
                {
                    Name = "Live Music",
                    Description =
                        "Attending a live music event or concert at a venue such as a bar, club, or music hall."
                },
                new ActivityType
                {
                    Name = "Wine Tasting",
                    Description = "Participating in a wine tasting event, usually at a winery or vineyard."
                },
                new ActivityType
                {
                    Name = "Pub Crawl",
                    Description = "Visiting multiple pubs or bars with friends as part of a social activity."
                },

                // Fitness & Outdoor Activities
                new ActivityType
                {
                    Name = "Fitness Activity",
                    Description = "Engaging in physical exercise or fitness routines at a gym, park, or home."
                },
                new ActivityType
                {
                    Name = "Sports Activity",
                    Description =
                        "Participating in sports like running, cycling, or team sports at a designated location."
                },
                new ActivityType
                    { Name = "Running", Description = "Running outdoors in a park, along streets, or at a track." },
                new ActivityType
                {
                    Name = "Jogging", Description = "Light running, usually in the morning or for fitness purposes."
                },
                new ActivityType
                {
                    Name = "Cycling", Description = "Riding a bicycle in parks, on roads, or designated cycling paths."
                },
                new ActivityType
                    { Name = "Mountain Biking", Description = "Cycling on rough terrain or mountain trails." },
                new ActivityType { Name = "Hiking", Description = "Hiking in nature, mountains, or parks." },
                new ActivityType
                    { Name = "Swimming", Description = "Swimming at a beach, pool, or other swimming facility." },
                new ActivityType { Name = "Skiing", Description = "Skiing at a ski resort or on a snowy mountain." },
                new ActivityType
                    { Name = "Snowboarding", Description = "Snowboarding on snowy slopes or at a resort." },
                new ActivityType
                    { Name = "Surfing", Description = "Surfing at the beach or at a designated surf spot." },
                new ActivityType { Name = "Fishing", Description = "Fishing at a lake, river, or the sea." },
                new ActivityType
                {
                    Name = "Camping",
                    Description = "Setting up a tent and spending time outdoors in a forest, park, or campground."
                },
                new ActivityType { Name = "Kayaking", Description = "Paddling a kayak on a river, lake, or ocean." },
                new ActivityType
                    { Name = "Rafting", Description = "Engaging in rafting on rivers or white-water locations." },
                new ActivityType
                {
                    Name = "Rock Climbing",
                    Description = "Climbing rocks or indoor climbing walls for fitness or sport."
                },
                new ActivityType
                    { Name = "Horseback Riding", Description = "Riding horses in a park, ranch, or trail." },
                new ActivityType
                {
                    Name = "Bungee Jumping",
                    Description = "Engaging in bungee jumping from a bridge, cliff, or other location."
                },
                new ActivityType
                    { Name = "Paragliding", Description = "Flying a parachute or glider for sport or leisure." },
                new ActivityType
                {
                    Name = "Scuba Diving", Description = "Diving underwater using scuba equipment in oceans or pools."
                },
                new ActivityType
                {
                    Name = "Snorkeling",
                    Description = "Swimming and observing underwater life while using a snorkel and mask."
                },
                new ActivityType { Name = "Boating", Description = "Traveling by a boat." },

                // Cultural & Educational Activities
                new ActivityType
                {
                    Name = "Sightseeing Tour",
                    Description = "Guided or independent tours of historical sites or tourist attractions."
                },
                new ActivityType
                    { Name = "City Walk", Description = "Exploring a city on foot, typically in a casual manner." },
                new ActivityType
                {
                    Name = "Food Tasting",
                    Description = "Trying different foods, usually as part of a tour or special event."
                },
                new ActivityType
                {
                    Name = "Historical Site Visit", Description = "Visiting a historical monument or heritage site."
                },
                new ActivityType
                {
                    Name = "Art Gallery Visit", Description = "Visiting an art gallery or museum to view exhibitions."
                },
                new ActivityType
                {
                    Name = "Class",
                    Description = "Attending a class or lecture at a school, university, or educational institution."
                },
                new ActivityType
                {
                    Name = "Study Session", Description = "Studying at a library, café, or any other study location."
                },
                new ActivityType
                {
                    Name = "Research",
                    Description = "Conducting research or experiments at a research facility or designated location."
                },
                new ActivityType
                {
                    Name = "Skills Development",
                    Description = "Taking part in skills-building activities or training sessions at a location."
                },
                new ActivityType
                {
                    Name = "Study Break",
                    Description = "Taking a break from studying at a café, park, or other relaxed location."
                },

                // Personal & Health Activities
                new ActivityType
                {
                    Name = "Doctor Appointment",
                    Description = "Attending a medical appointment at a clinic, doctor's office, or hospital."
                },
                new ActivityType
                {
                    Name = "Mental Health Session",
                    Description = "Attending therapy or counseling sessions at a wellness center or clinic."
                },
                new ActivityType
                {
                    Name = "Wellness Tracking",
                    Description =
                        "Monitoring health or wellness activities at specific locations such as gyms or clinics."
                },

                // Work & Business Activities
                new ActivityType { Name = "At Work", Description = "Working in workplace." },
                new ActivityType { Name = "Ouside Work", Description = "Working out of workplace." },
                new ActivityType
                {
                    Name = "Business Trip",
                    Description = "Traveling for work-related purposes, including meetings and client visits."
                },
                new ActivityType
                {
                    Name = "Collaboration",
                    Description =
                        "Working with others on a project or task at a location like a meeting room or office."
                },
                new ActivityType
                {
                    Name = "Conference",
                    Description = "Attending a business or industry conference at a designated venue."
                },
                new ActivityType
                {
                    Name = "Meeting",
                    Description = "Attending a meeting, either work-related or personal, at a specific location."
                },

                // Volunteering & Charity Activities
                new ActivityType
                {
                    Name = "Volunteer Work",
                    Description = "Engaging in voluntary work or helping out in community service."
                },
                new ActivityType
                {
                    Name = "Charity Work",
                    Description = "Engaging in community service or charity work at a designated location."
                },

                // Religious Activities
                new ActivityType
                {
                    Name = "Religious Event",
                    Description = "Attending a religious ceremony such as a service at a church, mosque, or temple."
                },
                new ActivityType
                    { Name = "Pilgrimage", Description = "Traveling to a religious or spiritual destination." },
                new ActivityType
                {
                    Name = "Prayer",
                    Description = "Participating in religious prayer activities at a place of worship or at home."
                },

                // Other Miscellaneous Activities
                new ActivityType { Name = "Photography", Description = "Taking photographs." },
                new ActivityType { Name = "Walking", Description = "Taking a walk." },
                new ActivityType
                {
                    Name = "Pet Care",
                    Description = "Taking care of pets, such as walking dogs or visiting a veterinarian."
                },
                new ActivityType { Name = "Hotel Check-in", Description = "Checking into" }
            );

            await dbContext.SaveChangesAsync();
        }
    }
}