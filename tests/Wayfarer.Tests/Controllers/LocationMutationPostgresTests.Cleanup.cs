using Microsoft.EntityFrameworkCore;
using Moq;
using Quartz;
using Wayfarer.Jobs;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Services.LocationMutations;
using Xunit;
using Wayfarer.Tests.Infrastructure;

namespace Wayfarer.Tests.Controllers;

/// <summary>A12: bounded cleanup progress, busy-account deferral and retained evidence eligibility.</summary>
public sealed partial class LocationMutationPostgresTests
{
    /// <summary>Twenty real owner transactions stop the run; a persisted cursor advances beyond a busy owner on the next wake.</summary>
    [PostgresFact]
    public async Task CleanupDefersBusyOwnerAndAdvancesWithinTransactionBudget()
    {
        var owners = new List<string>();
        for (var index = 0; index < 21; index++) owners.Add((await fixture.CreateUserAsync()).Id);
        owners.Sort(StringComparer.Ordinal);
        var expiry = DateTime.UtcNow.AddDays(-32);
        await using (var db = fixture.CreateContext())
        {
            foreach (var owner in owners) db.LocationMutationOperations.Add(Receipt(owner, expiry));
            await db.SaveChangesAsync();
        }
        await using var busy = fixture.CreateContext();
        await using var fence = await LocationDeletionFence.BeginAsync(busy, owners[0], default);
        await using var cleanup = fixture.CreateContext();
        var data = new JobDataMap(); // Same absent cursor state as fresh Quartz registration.
        var scheduler = new Mock<IScheduler>();
        var context = CleanupJobContext(data, scheduler.Object);
        var job = new LocationMutationCleanupJob(cleanup);
        await job.Execute(context);
        Assert.Equal("Completed", data.GetString("Status"));
        Assert.Equal("Deleted 19 eligible Location receipts", data.GetString("StatusMessage"));
        Assert.False(string.IsNullOrEmpty(data.GetString("CursorTime")));
        scheduler.Verify(s => s.ScheduleJob(It.Is<ITrigger>(t =>
            t.Key.Name == "LocationMutationCleanupFollowUp" && t.JobKey!.Name == "LocationMutationCleanupJob"),
            It.IsAny<CancellationToken>()), Times.Once);
        await job.Execute(context); // Retained JobDataMap resumes beyond the busy owner.
        Assert.Equal("Deleted 1 eligible Location receipts", data.GetString("StatusMessage"));
        Assert.Equal("", data.GetString("CursorTime"));
        Assert.Equal("", data.GetString("CursorOwner"));
        Assert.Equal("", data.GetString("CursorId"));
        Assert.True(await cleanup.LocationMutationOperations.AnyAsync(row => row.UserId == owners[0]));
        await fence!.RollbackAsync();
        var final = await new LocationMutationCleanup(cleanup).RunAsync(default);
        Assert.Equal(1, final.Deleted);
        Assert.False(await cleanup.LocationMutationOperations.AnyAsync(row => owners.Contains(row.UserId)));
    }

    /// <summary>Incomplete initial cursor tuples restart safely rather than skipping eligible receipts.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupJobIncompleteCursorStartsAtBeginning(bool includeId)
    {
        var owner = (await fixture.CreateUserAsync()).Id;
        await using var db = fixture.CreateContext();
        db.LocationMutationOperations.Add(Receipt(owner, DateTime.UtcNow.AddDays(-32)));
        await db.SaveChangesAsync();
        var data = new JobDataMap { ["CursorTime"] = DateTime.UtcNow.ToString("O") };
        if (includeId) data["CursorId"] = "1"; // Owner is still absent: this is not a valid continuation.
        var scheduler = new Mock<IScheduler>(MockBehavior.Strict);
        await new LocationMutationCleanupJob(db).Execute(CleanupJobContext(data, scheduler.Object));
        Assert.Equal("Completed", data.GetString("Status"));
        Assert.Equal("Deleted 1 eligible Location receipts", data.GetString("StatusMessage"));
        Assert.False(await db.LocationMutationOperations.AnyAsync(row => row.UserId == owner));
    }

    /// <summary>Cancellation reaches the cleanup service and retains Quartz's cancelled outcome.</summary>
    [PostgresFact]
    public async Task CleanupJobCancellationIsReportedAndPropagated()
    {
        await using var db = fixture.CreateContext();
        var data = new JobDataMap();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = CleanupJobContext(data, Mock.Of<IScheduler>(), cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LocationMutationCleanupJob(db).Execute(context));
        Assert.Equal("Cancelled", data.GetString("Status"));
    }

    /// <summary>Invoke the actual job with Quartz's durable map and scheduler boundary.</summary>
    private static IJobExecutionContext CleanupJobContext(JobDataMap data, IScheduler scheduler, CancellationToken token = default)
    {
        var detail = new Mock<IJobDetail>();
        detail.SetupGet(d => d.JobDataMap).Returns(data);
        var context = new Mock<IJobExecutionContext>();
        context.SetupGet(c => c.JobDetail).Returns(detail.Object);
        context.SetupGet(c => c.Scheduler).Returns(scheduler);
        context.SetupGet(c => c.CancellationToken).Returns(token);
        return context.Object;
    }

    /// <summary>Pages remain bounded at 200; a future retention threshold and ingestion markers survive cleanup.</summary>
    [PostgresFact]
    public async Task CleanupPagesDoNotRemoveFreshReceiptsOrDeletionMarkers()
    {
        var (owner, _, _) = await SeedAsync();
        var old = DateTime.UtcNow.AddDays(-32);
        await using var db = fixture.CreateContext();
        for (var index = 0; index < 201; index++) db.LocationMutationOperations.Add(Receipt(owner, old));
        var retained = Receipt(owner, old); retained.ReceiptRetainUntilUtc = DateTime.UtcNow.AddHours(1);
        db.LocationMutationOperations.Add(retained);
        db.Set<LocationDeletionMarker>().Add(new() { UserId = owner, IdempotencyKey = Guid.NewGuid() });
        await db.SaveChangesAsync();
        var result = await new LocationMutationCleanup(db).RunAsync(default);
        Assert.Equal(201, result.Deleted);
        Assert.Equal(retained.Id, await db.LocationMutationOperations.Where(row => row.UserId == owner).Select(row => row.Id).SingleAsync());
        Assert.Single(await db.Set<LocationDeletionMarker>().Where(row => row.UserId == owner).ToListAsync());
    }

    /// <summary>Compact old binding without a Location FK or request body.</summary>
    private static LocationMutationOperation Receipt(string owner, DateTime expiry) => new()
    {
        UserId = owner, BaseId = Guid.NewGuid(), ActionId = Guid.NewGuid(), LocationId = 421,
        Adapter = "owner", Kind = "delete", Normalization = "location-values-1", Fingerprint = "sha256:" + new string('a', 64),
        ExpiresAtUtc = expiry, AcceptedAtUtc = expiry.AddMinutes(-15), ExecuteBeforeUtc = expiry,
        ReceiptRetainUntilUtc = expiry.AddDays(30), ExecutorNonce = Guid.NewGuid(), State = "pending"
    };
}
