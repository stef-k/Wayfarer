using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;
using Wayfarer.Models;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Integration;

/// <summary>
/// Tests for auto-deleting groups when the last active member leaves or is removed.
/// Empty groups are always automatically deleted to prevent orphaned data.
/// </summary>
public class AutoDeleteEmptyGroupsTests
{
    private static ApplicationDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: System.Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new ApplicationDbContext(opts, new ServiceCollection().BuildServiceProvider());
    }

    [Fact(DisplayName = "Auto-delete: delete group when last member leaves")]
    public async Task AutoDelete_DeletesOnLastLeave()
    {
        using var db = MakeDb();
        // Seed users and settings
        var owner = new ApplicationUser { Id = "u1", UserName = "u1", DisplayName = "u1" };
        db.Users.Add(owner);
        db.ApplicationSettings.Add(new ApplicationSettings
        {
            Id = 1,
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
}

