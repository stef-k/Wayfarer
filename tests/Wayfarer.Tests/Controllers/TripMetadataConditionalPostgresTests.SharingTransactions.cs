using System.Data.Common;
using System.Net;
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
