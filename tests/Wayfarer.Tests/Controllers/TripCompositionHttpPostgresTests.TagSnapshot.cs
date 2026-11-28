using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class TripCompositionHttpPostgresTests
{
    /// <summary>Compose must refuse a replacement based on a membership snapshot older than a tag-only commit.</summary>
    [PostgresFact]
    public async Task ApplyCannotReplaceTagsFromAStaleSerializableSnapshot()
    {
        var owner = await fixture.CreateUserAsync();
        var a = new Trip { Id = Guid.NewGuid(), UserId = owner.Id, Name = "Source A" };
        var b = new Trip { Id = Guid.NewGuid(), UserId = owner.Id, Name = "Source B" };
        await using var db = fixture.CreateContext();
        db.Trips.AddRange(a, b);
        await db.SaveChangesAsync();
        foreach (var source in new[] { a, b }) fixture.RegisterTrip(source.Id);
        var workspace = await new TripCompositionService(db).CreateAsync(owner.Id, a.Id, b.Id, null, null);
        fixture.RegisterTrip(workspace.TripId);
        var trip = await db.Trips.AsNoTracking().SingleAsync(t => t.Id == workspace.TripId);
        var gate = new TagSnapshotGate();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => fixture.CreateContext(gate));
            services.AddSingleton(Mock.Of<ICacheWarmupScheduler>());
            services.AddSingleton(Mock.Of<IIconColorProvider>());
            services.AddSingleton(Mock.Of<ITripMapThumbnailGenerator>());
        });
        using var browser = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(browser, app.Services, owner.Id);
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(browser);
        var name = "race" + Guid.NewGuid().ToString("N");
        var append = TripControlWrite.Create(trip.Id, $"/User/Trip/{trip.Id}/Tags/attach/conditional",
            HttpMethod.Post, "tags-attach", true, new { tags = new[] { name } });
        var permit = await append.PermitAsync(browser, trip);
        gate.Armed = true;
        var pending = browser.PostAsJsonAsync($"/api/trip-compositions/{workspace.CompositionId}/actions", new
        {
            source = "b",
            items = new[] { new { kind = "trip", sourceId = b.Id, decision = "reuse", destinationId = trip.Id,
                fields = new { tags = new { operation = "edit", expected = Array.Empty<string>(), value = new[] { "Compose tag" } } } } }
        });
        try
        {
            await gate.Read.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var committed = await append.DispatchAsync(browser, permit);
            committed.EnsureSuccessStatusCode();
            var tag = await db.Tags.AsNoTracking().SingleAsync(t => t.Slug == name);
            fixture.RegisterTag(tag);
        }
        finally { gate.Resume.TrySetResult(); }
        using var refused = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("composition-contention", await refused.Content.ReadAsStringAsync());
        var saved = await db.Trips.AsNoTracking().Include(t => t.Tags).SingleAsync(t => t.Id == trip.Id);
        Assert.Equal(name, Assert.Single(saved.Tags).Slug);
        Assert.Equal(trip.UpdatedAt, saved.UpdatedAt);
    }

    /// <summary>Pauses only the first armed membership SELECT after PostgreSQL has opened its result.</summary>
    private sealed class TagSnapshotGate : DbCommandInterceptor
    {
        internal bool Armed { get; set; }
        internal TaskCompletionSource Read { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Leaves the original Serializable transaction and snapshot in place.</summary>
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("\"TripTags\"", StringComparison.Ordinal))
            {
                Armed = false;
                Read.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
