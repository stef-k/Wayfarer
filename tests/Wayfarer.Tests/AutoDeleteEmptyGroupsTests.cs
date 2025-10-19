using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;
using Wayfarer.Models;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests;

/// <summary>
/// Tests for auto-deleting groups when the last active member leaves or is removed.
/// </summary>
public class AutoDeleteEmptyGroupsTests
{
    private static ApplicationDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: System.Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(opts, new ServiceCollection().BuildServiceProvider());
    }

    [Fact(DisplayName = "Auto-delete enabled: delete when last member leaves")]
    public async Task AutoDelete_WhenEnabled_DeletesOnLastLeave()
    {
        using var db = MakeDb();
        // Seed users and settings
        var owner = new ApplicationUser { Id = "u1", UserName = "u1", DisplayName = "u1" };
        db.Users.Add(owner);
        db.ApplicationSettings.Add(new ApplicationSettings
        {
            Id = 1,
            AutoDeleteEmptyGroups = true,
            LocationTimeThresholdMinutes = 5,
            LocationDistanceThresholdMeters = 15,
            IsRegistrationOpen = false,
            UploadSizeLimitMB = ApplicationSettings.DefaultUploadSizeLimitMB,
            MaxCacheTileSizeInMB = ApplicationSettings.DefaultMaxCacheTileSizeInMB
        });
        await db.SaveChangesAsync();

        var svc = new GroupService(db);
        var g = await svc.CreateGroupAsync(owner.Id, "G1", null);

        // Owner leaves; no other active members remain, should delete group
        await svc.LeaveGroupAsync(g.Id, owner.Id);

        Assert.False(await db.Groups.AnyAsync(x => x.Id == g.Id));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "GroupDelete"));
    }

    [Fact(DisplayName = "Auto-delete disabled: keep group when last member leaves")]
    public async Task AutoDelete_WhenDisabled_KeepsGroupOnLastLeave()
    {
        using var db = MakeDb();
        // Seed users and settings (disabled)
        var owner = new ApplicationUser { Id = "u2", UserName = "u2", DisplayName = "u2" };
        db.Users.Add(owner);
        db.ApplicationSettings.Add(new ApplicationSettings
        {
            Id = 1,
            AutoDeleteEmptyGroups = false,
            LocationTimeThresholdMinutes = 5,
            LocationDistanceThresholdMeters = 15,
            IsRegistrationOpen = false,
            UploadSizeLimitMB = ApplicationSettings.DefaultUploadSizeLimitMB,
            MaxCacheTileSizeInMB = ApplicationSettings.DefaultMaxCacheTileSizeInMB
        });
        await db.SaveChangesAsync();

        var svc = new GroupService(db);
        var g = await svc.CreateGroupAsync(owner.Id, "G2", null);

        // Owner leaves; feature disabled so group should remain
        await svc.LeaveGroupAsync(g.Id, owner.Id);

        Assert.True(await db.Groups.AnyAsync(x => x.Id == g.Id));
        // Still records leave audit, but no delete audit
        Assert.False(await db.AuditLogs.AnyAsync(a => a.Action == "GroupDelete" && a.Details.Contains("G2")));
    }
}

