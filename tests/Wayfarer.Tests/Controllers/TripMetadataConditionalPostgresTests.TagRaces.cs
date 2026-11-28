using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using static Wayfarer.Tests.Controllers.TripMetadataReceiptHttp;

namespace Wayfarer.Tests.Controllers;

public sealed partial class TripMetadataConditionalPostgresTests
{
    /// <summary>Orders a real join against catalogue orphan deletion in both directions.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task V2_JoinAndOrphanDeletionSerialize(bool attachFirst)
    {
        var source = await SeedAsync();
        var destination = await SeedAsync();
        var tag = NewTag();
        await using var db = fixture.CreateContext();
        db.Trips.Attach(source);
        source.Tags.Add(tag);
        db.Tags.Add(tag);
        await db.SaveChangesAsync();
        var pause = new TagCommitGate();
        await using var app = await StartAsync(db, [pause, pause.WaitProbe]);
        using var from = await WebAsync(app, source.UserId);
        using var to = await WebAsync(app, destination.UserId);
        var detach = TripControlWrite.Create(source.Id, $"/User/Trip/{source.Id}/Tags/detach/conditional",
            HttpMethod.Post, "tags-detach", true, new { slug = tag.Slug });
        var attach = TripControlWrite.Create(destination.Id, $"/User/Trip/{destination.Id}/Tags/attach/conditional",
            HttpMethod.Post, "tags-attach", true, new { tags = new[] { tag.Name } });
        var detachPermit = await detach.PermitAsync(from, source);
        var attachPermit = await attach.PermitAsync(to, destination);
        pause.Armed = true;
        var first = attachFirst ? attach.DispatchAsync(to, attachPermit) : detach.DispatchAsync(from, detachPermit);
        Task<HttpResponseMessage>? second = null;
        try
        {
            await pause.AtCommit.Task.WaitAsync(TimeSpan.FromSeconds(10));
            pause.WaitProbe.Armed = true;
            second = attachFirst ? detach.DispatchAsync(from, detachPermit) : attach.DispatchAsync(to, attachPermit);
            await WaitForBlockAsync(await pause.WaitProbe.Waiter.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { pause.Resume.TrySetResult(); }
        using var firstReply = await first.WaitAsync(TimeSpan.FromSeconds(10));
        using var secondReply = await (second ?? throw new InvalidOperationException("Both writers must dispatch.")).WaitAsync(TimeSpan.FromSeconds(10));
        var removed = await EvidenceAsync(attachFirst ? secondReply : firstReply, detachPermit, "applied");
        var attached = await EvidenceAsync(attachFirst ? firstReply : secondReply, attachPermit, "applied");
        var id = Assert.Single(attached.GetProperty("tagResult").GetProperty("resultingTags").EnumerateArray()).GetProperty("id").GetGuid();
        fixture.RegisterTag(new Tag { Id = id });
        Assert.Equal(attachFirst, id == tag.Id);
        Assert.Equal(attachFirst ? 0 : 1, removed.GetProperty("tagResult").GetProperty("deletedCatalogue").GetArrayLength());
        Assert.True(await db.Trips.AsNoTracking().AnyAsync(t => t.Id == destination.Id && t.Tags.Any(t => t.Id == id)));
        Assert.False(await db.Trips.AsNoTracking().AnyAsync(t => t.Id == source.Id && t.Tags.Any()));
    }

    /// <summary>Pauses a locked domain transaction immediately before commit, leaving the second request observable.</summary>
    private sealed class TagCommitGate : DbTransactionInterceptor
    {
        internal bool Armed { get; set; }
        internal TaskCompletionSource AtCommit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CatalogueWaitProbe WaitProbe { get; } = new();

        /// <summary>Waits only for the armed original operation's commit.</summary>
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData data,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result;
            Armed = false;
            AtCommit.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    /// <summary>Identifies the real waiter on PostgreSQL catalogue authority.</summary>
    private sealed class CatalogueWaitProbe : DbCommandInterceptor
    {
        internal bool Armed { get; set; }
        internal TaskCompletionSource<int> Waiter { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Captures the competing operation's backend PID.</summary>
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("Wayfarer.TripTags.Catalogue", StringComparison.Ordinal))
                Waiter.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID);
            return ValueTask.FromResult(result);
        }
    }
}
