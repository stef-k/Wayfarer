using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;
using Browser = Wayfarer.Tests.Controllers.ManagerAccountAuthorizationPostgresTests.Browser;

namespace Wayfarer.Tests.Controllers;

/// <summary>Serializes genuine Manager token requests with Admin promotion in a class-owned disposable database.</summary>
public sealed class ManagerTokenConcurrencyPostgresTests(PostgresMigrationTestFixture fixture)
    : TestBase, IClassFixture<PostgresMigrationTestFixture>
{
    /// <summary>Once authorized, the Manager holds the account through the token write or missing-token rollback.</summary>
    [Theory]
    [InlineData("Regenerate", false)]
    [InlineData("Regenerate", true)]
    [InlineData("Delete", false)]
    [InlineData("Create", true)]
    public async Task AuthorizedMutation_BlocksPromotionUntilFinished(string action, bool initiallyAbsent)
    {
        var pause = new CommandPause(afterEligibility: true);
        await using var manager = await StartAsync("Manager", pause);
        await using var admin = await StartAsync("Admin");
        var target = await manager.UserAsync("User");
        var name = action == "Delete" ? "named" : ApiTokenService.ConnectionTokenName;
        var token = initiallyAbsent ? null : await manager.TokenAsync(target, name);
        var before = await SnapshotAsync(target.Id);
        pause.Armed = true;
        var mutation = manager.PostAsync("ApiToken/" + action, ("id", target.Id), ("name", name),
            ("tokenId", (token?.Id ?? 0).ToString()));
        Task<HttpResponseMessage>? promotion = null;
        try
        {
            await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            promotion = PromoteAsync(admin, target);
            await WaitForBlockedPromotionAsync(pause.BackendPid);
            Assert.False(promotion.IsCompleted);
            Assert.Equal(before, await SnapshotAsync(target.Id));
            pause.Release.TrySetResult();
            using var result = await mutation.WaitAsync(TimeSpan.FromSeconds(15));
            AssertRedirect(result);
            using var promoted = await promotion.WaitAsync(TimeSpan.FromSeconds(15));
            AssertRedirect(promoted);

            await using (var fresh = fixture.CreateContext())
            {
                var changed = await fresh.ApiTokens.SingleOrDefaultAsync(t => t.UserId == target.Id && t.Name == name);
                if (action == "Delete" || action == "Regenerate" && initiallyAbsent) Assert.Null(changed);
                else
                {
                    Assert.NotNull(changed);
                    Assert.Null(changed.Token);
                    Assert.False(string.IsNullOrWhiteSpace(changed.TokenHash));
                    Assert.NotEqual(token?.TokenHash, changed.TokenHash);
                }
            }
            // In the strongest counterexample no credential existed before this Admin-only creation.
            await IssueAdminCredentialAsync(admin, target, name);
            var afterAdmin = await SnapshotAsync(target.Id);
            using var denied = await manager.PostAsync("ApiToken/" + action, ("id", target.Id), ("name", name),
                ("tokenId", (token?.Id ?? 1).ToString()));
            AssertDenied(denied);
            Assert.Equal(afterAdmin, await SnapshotAsync(target.Id));
        }
        finally
        {
            pause.Release.TrySetResult();
            await DrainAsync(mutation);
            if (promotion != null) await DrainAsync(promotion);
        }
    }

    /// <summary>A request begun for a User rechecks fresh roles after a concurrent Admin promotion wins the lock.</summary>
    [Theory]
    [InlineData("Regenerate")]
    [InlineData("Delete")]
    [InlineData("Create")]
    public async Task PromotionBeforeLock_DeniesMutationAndPreservesAdminCredential(string action)
    {
        var pause = new CommandPause(afterEligibility: false);
        await using var manager = await StartAsync("Manager", pause);
        await using var admin = await StartAsync("Admin");
        var target = await manager.UserAsync("User");
        var name = action == "Delete" ? "named" : ApiTokenService.ConnectionTokenName;
        var token = action == "Delete" ? await manager.TokenAsync(target, name) : null;
        pause.Armed = true;
        var mutation = manager.PostAsync("ApiToken/" + action, ("id", target.Id),
            ("name", name), ("tokenId", (token?.Id ?? 0).ToString()));
        try
        {
            await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var promoted = await PromoteAsync(admin, target);
            AssertRedirect(promoted);
            await IssueAdminCredentialAsync(admin, target, name);
            var afterAdmin = await SnapshotAsync(target.Id);
            pause.Release.TrySetResult();
            using var response = await mutation.WaitAsync(TimeSpan.FromSeconds(15));
            AssertDenied(response);
            Assert.Equal(afterAdmin, await SnapshotAsync(target.Id));
        }
        finally
        {
            pause.Release.TrySetResult();
            await DrainAsync(mutation);
        }
    }

    /// <summary>Promotes through the unchanged production Admin role handler with genuine cookies and antiforgery.</summary>
    private static Task<HttpResponseMessage> PromoteAsync(Browser admin, ApplicationUser target) =>
        admin.PostAsync("/Admin/Users/Edit", ("Id", target.Id), ("UserName", target.UserName!),
            ("DisplayName", "Promoted by Admin"), ("Role", "Admin"), ("IsActive", "true"), ("IsProtected", "true"));

    /// <summary>Issues a fresh Admin credential and verifies committed roles, hashes and account state independently.</summary>
    private async Task IssueAdminCredentialAsync(Browser admin, ApplicationUser target, string name)
    {
        await using var fresh = fixture.CreateContext();
        var user = await fresh.Users.SingleAsync(u => u.Id == target.Id);
        Assert.True(user.IsProtected);
        Assert.True(user.IsActive);
        Assert.Equal("Promoted by Admin", user.DisplayName);
        Assert.Equal(target.PasswordHash, user.PasswordHash);
        Assert.NotEqual(target.ConcurrencyStamp, user.ConcurrencyStamp);
        Assert.Equal(new[] { "Admin" }, await RolesAsync(fresh, target.Id));
        var oldHash = await fresh.ApiTokens.Where(t => t.UserId == target.Id && t.Name == name)
            .Select(t => t.TokenHash).SingleOrDefaultAsync();
        using var issued = await admin.PostAsync("/Admin/ApiToken/" + (oldHash == null ? "Create" : "Regenerate"),
            ("id", target.Id), ("name", name));
        AssertRedirect(issued);
        await using var final = fixture.CreateContext();
        var credential = await final.ApiTokens.SingleAsync(t => t.UserId == target.Id && t.Name == name);
        Assert.Null(credential.Token);
        Assert.False(string.IsNullOrWhiteSpace(credential.TokenHash));
        Assert.NotEqual(oldHash, credential.TokenHash);
    }

    /// <summary>Observes a real PostgreSQL account-write wait whose blocker is the paused Manager connection.</summary>
    private async Task WaitForBlockedPromotionAsync(int managerPid)
    {
        await using var observer = new NpgsqlConnection(fixture.ConnectionString);
        await observer.OpenAsync();
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                WHERE datname = current_database() AND wait_event_type = 'Lock'
                AND @manager = ANY(pg_blocking_pids(pid)) AND query LIKE '%UPDATE "AspNetUsers"%')
                """, observer);
            command.Parameters.AddWithValue("manager", managerPid);
            if ((bool)(await command.ExecuteScalarAsync())!) return;
            await Task.Delay(50);
        }
        Assert.Fail("Admin promotion never waited on the Manager's account-row lock.");
    }

    /// <summary>Captures all account properties, complete roles and credential versions using a fresh context.</summary>
    private async Task<string> SnapshotAsync(string id)
    {
        await using var fresh = fixture.CreateContext();
        var user = await fresh.Users.SingleAsync(u => u.Id == id);
        return JsonSerializer.Serialize(new
        {
            User = fresh.Entry(user).Properties.ToDictionary(p => p.Metadata.Name, p => p.CurrentValue),
            Roles = await RolesAsync(fresh, id),
            Tokens = await fresh.ApiTokens.Where(t => t.UserId == id).OrderBy(t => t.Id)
                .Select(t => new { t.Id, t.UserId, t.Name, t.Token, t.TokenHash, t.CreatedAt }).ToArrayAsync()
        });
    }

    /// <summary>Reads the complete persisted role set without either request's change tracker.</summary>
    private static Task<string[]> RolesAsync(ApplicationDbContext db, string id) =>
        (from membership in db.UserRoles join role in db.Roles on membership.RoleId equals role.Id
         where membership.UserId == id orderby role.Name select role.Name!).ToArrayAsync();

    /// <summary>Reuses the established genuine-cookie transport with the fixture's supported interception seam.</summary>
    private Task<Browser> StartAsync(string role, params IInterceptor[] interceptors) =>
        Browser.StartAsync(fixture, CreateTestDirectory(), role, interceptors);

    /// <summary>Successful administration retains its existing redirect without secret presentation.</summary>
    private static void AssertRedirect(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain("AccessDenied", response.Headers.Location!.OriginalString);
    }

    /// <summary>Real cookie authorization redirects forbidden targets to AccessDenied.</summary>
    private static void AssertDenied(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("AccessDenied", response.Headers.Location!.OriginalString);
    }

    /// <summary>Drains owned requests after opening the scheduling gate without masking a primary assertion failure.</summary>
    private static async Task DrainAsync(Task<HttpResponseMessage> pending)
    {
        try { using var response = await pending.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch { /* The host and class-owned database still dispose after the primary failure. */ }
    }

    /// <summary>Pauses scheduling only, returning the original production SQL result unchanged.</summary>
    private sealed class CommandPause(bool afterEligibility) : DbCommandInterceptor
    {
        internal bool Armed { get; set; }
        internal int BackendPid { get; private set; }
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Stops after the actual complete-role SELECT while the Manager owns its transaction.</summary>
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data,
            DbDataReader reader, CancellationToken cancellationToken = default)
        {
            if (afterEligibility && command.CommandText.Contains("AspNetUserRoles")
                && command.CommandText.Contains("count(*)", StringComparison.OrdinalIgnoreCase))
                await PauseAsync(command, cancellationToken);
            return reader;
        }

        /// <summary>Lets Admin win just before the Manager's actual account-lock command executes.</summary>
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!afterEligibility && command.CommandText.Contains("AspNetUsers") && command.CommandText.Contains("FOR UPDATE"))
                await PauseAsync(command, cancellationToken);
            return result;
        }

        /// <summary>One-shot bounded scheduling gate; the test's finally always releases waiting requests.</summary>
        private async Task PauseAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (!Armed) return;
            Armed = false;
            BackendPid = ((NpgsqlConnection)command.Connection!).ProcessID;
            Reached.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
    }
}
