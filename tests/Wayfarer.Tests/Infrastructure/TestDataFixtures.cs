using Wayfarer.Models;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>
/// Provides factory methods for creating test data entities.
/// </summary>
public static class TestDataFixtures
{
    private static int _userCounter;
    private static int _groupCounter;
    private static int _tripCounter;
    private static int _locationCounter;

    /// <summary>
    /// Creates a test user with default values.
    /// </summary>
    /// <param name="id">The user ID. If null, a unique ID is generated.</param>
    /// <param name="username">The username. If null, derived from ID.</param>
    /// <param name="displayName">The display name. If null, derived from username.</param>
    /// <param name="isActive">Whether the user is active. Defaults to true.</param>
    /// <returns>A new ApplicationUser instance.</returns>
    public static ApplicationUser CreateUser(
        string? id = null,
        string? username = null,
        string? displayName = null,
        bool isActive = true)
    {
        var counter = Interlocked.Increment(ref _userCounter);
        id ??= $"user-{counter}";
        username ??= $"testuser{counter}";
        displayName ??= $"Test User {counter}";

        return new ApplicationUser
        {
            Id = id,
            UserName = username,
            DisplayName = displayName,
            Email = $"{username}@test.com",
            IsActive = isActive,
            EmailConfirmed = true
        };
    }

    /// <summary>
    /// Creates a test API token for a user.
    /// </summary>
    /// <param name="user">The user who owns the token.</param>
    /// <param name="token">The token value. If null, a unique token is generated.</param>
    /// <param name="name">The token name. Defaults to "test-token".</param>
    /// <returns>A new ApiToken instance.</returns>
    public static ApiToken CreateApiToken(
        ApplicationUser user,
        string? token = null,
        string name = "test-token")
    {
        token ??= $"token-{Guid.NewGuid():N}";

        return new ApiToken
        {
            Name = name,
            Token = token,
            User = user,
            UserId = user.Id,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Creates a test group.
    /// </summary>
    /// <param name="owner">The group owner.</param>
    /// <param name="name">The group name. If null, a unique name is generated.</param>
    /// <param name="groupType">The group type (e.g., "Friends", "Organization"). Defaults to null.</param>
    /// <returns>A new Group instance.</returns>
    public static Group CreateGroup(
        ApplicationUser owner,
        string? name = null,
        string? groupType = null)
    {
        var counter = Interlocked.Increment(ref _groupCounter);
        name ??= $"Test Group {counter}";

        return new Group
        {
            Id = Guid.NewGuid(),
            Name = name,
            OwnerUserId = owner.Id,
            Owner = owner,
            GroupType = groupType,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Creates a group membership for a user.
    /// </summary>
    /// <param name="group">The group.</param>
    /// <param name="user">The user to add as a member.</param>
    /// <param name="role">The membership role. Defaults to Member.</param>
    /// <param name="status">The membership status. Defaults to Active.</param>
    /// <returns>A new GroupMember instance.</returns>
    public static GroupMember CreateGroupMember(
        Group group,
        ApplicationUser user,
        string role = GroupMember.Roles.Member,
        string status = GroupMember.MembershipStatuses.Active)
    {
        return new GroupMember
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            Group = group,
            UserId = user.Id,
            User = user,
            Role = role,
            Status = status,
            JoinedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Creates a test trip.
    /// </summary>
    /// <param name="user">The trip owner.</param>
    /// <param name="name">The trip name. If null, a unique name is generated.</param>
    /// <param name="isPublic">Whether the trip is public. Defaults to false.</param>
    /// <returns>A new Trip instance.</returns>
    public static Trip CreateTrip(
        ApplicationUser user,
        string? name = null,
        bool isPublic = false)
    {
        var counter = Interlocked.Increment(ref _tripCounter);
        name ??= $"Test Trip {counter}";

        return new Trip
        {
            Id = Guid.NewGuid(),
            Name = name,
            UserId = user.Id,
            User = user,
            IsPublic = isPublic,
            UpdatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Creates a test trip with just a user ID (no User navigation property).
    /// </summary>
    /// <param name="userId">The user ID who owns the trip.</param>
    /// <param name="name">The trip name. If null, a unique name is generated.</param>
    /// <param name="isPublic">Whether the trip is public. Defaults to false.</param>
    /// <returns>A new Trip instance.</returns>
    public static Trip CreateTrip(
        string userId,
        string? name = null,
        bool isPublic = false)
    {
        var counter = Interlocked.Increment(ref _tripCounter);
        name ??= $"Test Trip {counter}";

        return new Trip
        {
            Id = Guid.NewGuid(),
            Name = name,
            UserId = userId,
            IsPublic = isPublic,
            UpdatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Creates a test region for a trip.
    /// </summary>
    /// <param name="trip">The parent trip.</param>
    /// <param name="name">The region name. If null, a unique name is generated.</param>
    /// <param name="displayOrder">The display order. Defaults to 0.</param>
    /// <returns>A new Region instance.</returns>
    public static Region CreateRegion(
        Trip trip,
        string? name = null,
        int displayOrder = 0)
    {
        name ??= $"Region {displayOrder + 1}";

        return new Region
        {
            Id = Guid.NewGuid(),
            Name = name,
            UserId = trip.UserId,
            TripId = trip.Id,
            Trip = trip,
            DisplayOrder = displayOrder
        };
    }

    /// <summary>
    /// Creates a test place in a region.
    /// </summary>
    /// <param name="region">The parent region.</param>
    /// <param name="name">The place name. If null, a unique name is generated.</param>
    /// <param name="latitude">The latitude. Defaults to 0.</param>
    /// <param name="longitude">The longitude. Defaults to 0.</param>
    /// <param name="displayOrder">The display order. Defaults to 0.</param>
    /// <returns>A new Place instance.</returns>
    public static Place CreatePlace(
        Region region,
        string? name = null,
        double latitude = 0,
        double longitude = 0,
        int displayOrder = 0)
    {
        name ??= $"Place {displayOrder + 1}";

        return new Place
        {
            Id = Guid.NewGuid(),
            Name = name,
            UserId = region.UserId,
            RegionId = region.Id,
            Region = region,
            Location = new NetTopologySuite.Geometries.Point(longitude, latitude) { SRID = 4326 },
            DisplayOrder = displayOrder
        };
    }

    /// <summary>
    /// Creates a test location for a user's timeline.
    /// </summary>
    /// <param name="user">The user who owns the location.</param>
    /// <param name="latitude">The latitude. If null, a random value is generated.</param>
    /// <param name="longitude">The longitude. If null, a random value is generated.</param>
    /// <param name="timestamp">The timestamp. If null, the current time is used.</param>
    /// <param name="timeZoneId">The time zone ID. Defaults to "UTC".</param>
    /// <returns>A new Location instance.</returns>
    public static Location CreateLocation(
        ApplicationUser user,
        double? latitude = null,
        double? longitude = null,
        DateTime? timestamp = null,
        string timeZoneId = "UTC")
    {
        var counter = Interlocked.Increment(ref _locationCounter);
        var random = new Random(counter);

        latitude ??= random.NextDouble() * 180 - 90;
        longitude ??= random.NextDouble() * 360 - 180;
        timestamp ??= DateTime.UtcNow.AddMinutes(-counter);

        return new Location
        {
            UserId = user.Id,
            Coordinates = new NetTopologySuite.Geometries.Point(longitude.Value, latitude.Value) { SRID = 4326 },
            Timestamp = timestamp.Value,
            LocalTimestamp = timestamp.Value,
            TimeZoneId = timeZoneId
        };
    }

    /// <summary>
    /// Creates multiple test locations for a user's timeline.
    /// </summary>
    /// <param name="user">The user who owns the locations.</param>
    /// <param name="count">The number of locations to create.</param>
    /// <param name="startTime">The start time for the first location. Defaults to current time.</param>
    /// <param name="intervalMinutes">The time interval between locations in minutes. Defaults to 5.</param>
    /// <returns>A list of Location instances.</returns>
    public static List<Location> CreateLocations(
        ApplicationUser user,
        int count,
        DateTime? startTime = null,
        int intervalMinutes = 5)
    {
        startTime ??= DateTime.UtcNow.AddMinutes(-count * intervalMinutes);
        var locations = new List<Location>();

        for (var i = 0; i < count; i++)
        {
            var location = CreateLocation(
                user,
                timestamp: startTime.Value.AddMinutes(i * intervalMinutes));
            locations.Add(location);
        }

        return locations;
    }

    /// <summary>
    /// Creates a group invitation.
    /// </summary>
    /// <param name="group">The group for the invitation.</param>
    /// <param name="inviter">The user who created the invitation.</param>
    /// <param name="invitee">The user being invited. If null, creates an email-only invitation.</param>
    /// <param name="email">The invited email. If null, uses the invitee's email.</param>
    /// <returns>A new GroupInvitation instance.</returns>
    public static GroupInvitation CreateGroupInvitation(
        Group group,
        ApplicationUser inviter,
        ApplicationUser? invitee = null,
        string? email = null)
    {
        email ??= invitee?.Email ?? $"invited-{Guid.NewGuid():N}@test.com";

        return new GroupInvitation
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            Group = group,
            InviterUserId = inviter.Id,
            Inviter = inviter,
            InviteeUserId = invitee?.Id,
            Invitee = invitee,
            InviteeEmail = email,
            Token = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
            Status = GroupInvitation.InvitationStatuses.Pending
        };
    }

    /// <summary>
    /// Resets all counters. Useful for test isolation if needed.
    /// </summary>
    public static void ResetCounters()
    {
        _userCounter = 0;
        _groupCounter = 0;
        _tripCounter = 0;
        _locationCounter = 0;
    }
}
