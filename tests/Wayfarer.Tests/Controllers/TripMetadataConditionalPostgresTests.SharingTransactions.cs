using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class TripMetadataConditionalPostgresTests
{
    /// <summary>Real Identity deletion must complete when its account lock precedes sharing's account read.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SharingAccountContention_DeletionCompletesWithoutDeadlock(bool editorRoute)
    {
        var trip = await SeedAsync();
        var gate = new SharingAccountGate();
        var probe = new MutationProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [gate, probe]);
        using var web = await WebAsync(app, trip.UserId);
        using var scope = app.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await manager.FindByIdAsync(trip.UserId))!;
        var sharing = ShareAsync(web, trip.Id, editorRoute, false);
        Task<IdentityResult>? deletion = null;
        try
        {
            // The request holds the Trip, but has not yet tried to lock the account.
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            deletion = manager.DeleteAsync(user);
            var deletePid = await gate.DeleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForBlockAsync(deletePid);
            await using (var diagnostic = fixture.CreateConnection())
            {
                await diagnostic.OpenAsync();
                await using var query = new NpgsqlCommand("SELECT $2 = ANY(pg_blocking_pids($1))", diagnostic);
                query.Parameters.AddWithValue(deletePid);
                query.Parameters.AddWithValue(gate.SharingProcessId);
                Assert.True((bool)(await query.ExecuteScalarAsync())!);
            }
            output.WriteLine($"Identity deletion PID {deletePid} waits on sharing PID {gate.SharingProcessId}'s Trip; resume account read.");
            gate.Release.TrySetResult();
            IdentityResult? deleted = null;
            var failure = await Record.ExceptionAsync(async () => deleted = await deletion.WaitAsync(TimeSpan.FromSeconds(15)));
            using var response = await sharing.WaitAsync(TimeSpan.FromSeconds(15));
            await using var verification = fixture.CreateContext();
            var accountExists = await verification.Users.AnyAsync(u => u.Id == trip.UserId);
            var tripExists = await verification.Trips.AnyAsync(t => t.Id == trip.Id);
            output.WriteLine($"editor={editorRoute}: deletion success={deleted?.Succeeded}; failure={failure}; sharing HTTP {(int)response.StatusCode}; account exists={accountExists}; Trip exists={tripExists}.");
            Assert.Null(failure);
            Assert.True(deleted!.Succeeded);
            await AssertSharingDeniedAsync(response, editorRoute, HttpStatusCode.Unauthorized);
            Assert.Empty(probe.Updates);
            Assert.False(accountExists || tripExists);
            Assert.False(await verification.ApiTokens.AnyAsync(t => t.UserId == trip.UserId));
            Assert.Empty(_warmup.Invocations);
            Assert.Empty(_thumbnails.Invocations);
        }
        finally
        {
            gate.Release.TrySetResult();
            using var response = await sharing.WaitAsync(TimeSpan.FromSeconds(15));
            // Drain a failed deletion too; the product exception is asserted and retained above.
            if (deletion != null) await Record.ExceptionAsync(async () => await deletion.WaitAsync(TimeSpan.FromSeconds(15)));
        }
    }

    /// <summary>A pending deactivation causes a no-write denial before the account transaction finishes.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SharingAccountContention_PendingDeactivationIsDenied(bool editorRoute)
    {
        var trip = await SeedAsync();
        var probe = new MutationProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, trip.UserId);
        await using var authority = fixture.CreateContext();
        await using var transaction = await authority.Database.BeginTransactionAsync();
        await authority.Users.Where(u => u.Id == trip.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        var pending = ShareAsync(web, trip.Id, editorRoute, false);
        try
        {
            // Deliberately await the denial before committing deactivation: sharing must not wait on this account.
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            await AssertSharingDeniedAsync(response, editorRoute, HttpStatusCode.Unauthorized);
            Assert.Empty(probe.Updates);
            await transaction.CommitAsync();
            await using var verification = fixture.CreateContext();
            var saved = await verification.Trips.AsNoTracking().SingleAsync(t => t.Id == trip.Id);
            Assert.Equal((trip.ShareProgressEnabled, trip.UpdatedAt, trip.Name, trip.Notes),
                (saved.ShareProgressEnabled, saved.UpdatedAt, saved.Name, saved.Notes));
            Assert.False(await verification.Users.Where(u => u.Id == trip.UserId).Select(u => u.IsActive).SingleAsync());
            output.WriteLine($"editor={editorRoute}: pending deactivation denied before account commit; sharing/freshness unchanged.");
        }
        finally
        {
            await transaction.DisposeAsync();
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>Successful account acquisition protects sharing until commit against later deactivation or deletion.</summary>
    [PostgresTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task SharingAccountProtectedThroughCommit(bool editorRoute, bool deleteAccount)
    {
        var trip = await SeedAsync();
        var account = new SharingAccountGate();
        account.Release.TrySetResult();
        var commit = new SharingCommitGate();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [account, commit]);
        using var web = await WebAsync(app, trip.UserId);
        using var scope = app.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await manager.FindByIdAsync(trip.UserId))!;
        var probe = new MutationProbe();
        await using var authority = fixture.CreateContext(probe);
        async Task ChangeAccountAsync()
        {
            if (deleteAccount) Assert.True((await manager.DeleteAsync(user)).Succeeded);
            else Assert.Equal(1, await authority.Users.Where(u => u.Id == trip.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false)));
        }
        var sharing = ShareAsync(web, trip.Id, editorRoute, false);
        Task? change = null;
        try
        {
            await commit.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            change = ChangeAccountAsync();
            Task started = deleteAccount ? account.DeleteStarted.Task : probe.UpdateStarted.Task;
            await started.WaitAsync(TimeSpan.FromSeconds(10));
            var changePid = deleteAccount ? await account.DeleteStarted.Task : probe.ProcessId;
            await WaitForBlockAsync(changePid);
            commit.Release.TrySetResult();
            using var response = await sharing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("success").GetBoolean());
            await change.WaitAsync(TimeSpan.FromSeconds(10));
            await using var verification = fixture.CreateContext();
            if (deleteAccount)
            {
                Assert.False(await verification.Users.AnyAsync(u => u.Id == trip.UserId));
                Assert.False(await verification.Trips.AnyAsync(t => t.Id == trip.Id));
            }
            else
            {
                Assert.False(await verification.Users.Where(u => u.Id == trip.UserId).Select(u => u.IsActive).SingleAsync());
                var saved = await verification.Trips.AsNoTracking().SingleAsync(t => t.Id == trip.Id);
                Assert.False(saved.ShareProgressEnabled);
                Assert.Equal((trip.Name, trip.Notes, trip.IsPublic), (saved.Name, saved.Notes, saved.IsPublic));
                Assert.True(saved.UpdatedAt >= trip.UpdatedAt);
            }
            output.WriteLine($"editor={editorRoute}, delete={deleteAccount}: account change waited until sharing commit; both completed.");
        }
        finally
        {
            commit.Release.TrySetResult();
            using var response = await sharing.WaitAsync(TimeSpan.FromSeconds(10));
            if (change != null) await change.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>A pre-commit failure rolls back the sharing SQL and freshness on either route.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SharingFailedCommit_RollsBackNarrowWrite(bool editorRoute)
    {
        var trip = await SeedAsync();
        var probe = new MutationProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe, new FailMetadataCommit()]);
        using var web = await WebAsync(app, trip.UserId);
        using var response = await ShareAsync(web, trip.Id, editorRoute, false);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Single(probe.Updates);
        await using var verification = fixture.CreateContext();
        var saved = await verification.Trips.AsNoTracking().SingleAsync(t => t.Id == trip.Id);
        Assert.Equal((trip.ShareProgressEnabled, trip.UpdatedAt, trip.Name, trip.Notes, trip.IsPublic),
            (saved.ShareProgressEnabled, saved.UpdatedAt, saved.Name, saved.Notes, saved.IsPublic));
        Assert.Empty(_warmup.Invocations);
        Assert.Empty(_thumbnails.Invocations);
    }

    /// <summary>Holds the existing EF pre-commit boundary after domain SQL without changing transaction behavior.</summary>
    private sealed class SharingCommitGate : DbTransactionInterceptor
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData data, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return result;
        }
    }

    /// <summary>Pauses only sharing's account read and observes the production Identity DELETE without adding locks.</summary>
    private sealed class SharingAccountGate : DbCommandInterceptor
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<int> DeleteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int SharingProcessId { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DELETE FROM \"AspNetUsers\""))
                DeleteStarted.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID);
            if (command.CommandText.Contains("AspNetUsers") && command.CommandText.Contains("FOR SHARE"))
            {
                SharingProcessId = ((NpgsqlConnection)command.Connection!).ProcessID;
                Started.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }
}
