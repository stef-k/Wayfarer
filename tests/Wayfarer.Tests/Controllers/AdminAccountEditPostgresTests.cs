using System.Data.Common;
using System.Net;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Wayfarer.Util;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Browser = Wayfarer.Tests.Controllers.ManagerAccountAuthorizationPostgresTests.Browser;

namespace Wayfarer.Tests.Controllers;

/// <summary>Routed Admin edits with real Identity, cookies, antiforgery and fresh PostgreSQL postconditions.</summary>
public sealed class AdminAccountEditPostgresTests(PostgresMigrationTestFixture fixture)
    : TestBase, IClassFixture<PostgresMigrationTestFixture>
{
    /// <summary>Omission, blanks, nonexistent roles and existing unsupported roles reject before any mutation.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DoesNotExist")]
    [InlineData("Unknown")]
    public async Task InvalidRole_RetainsCompletePersistedState(string? role)
    {
        await using var browser = await StartAsync();
        var target = await browser.UserAsync("Admin,User", protect: true);
        await browser.DependentsAsync(target);
        var before = await ReadAsync(target.Id);
        using var result = await EditAsync(browser, target, role);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Contains("text-danger", await result.Content.ReadAsStringAsync());
        Assert.True(before == await ReadAsync(target.Id));
    }

    /// <summary>All six directed changes retain legitimate Admin authority, including protected targets.</summary>
    [Theory]
    [InlineData("User", "Manager", false)]
    [InlineData("User", "Admin", false)]
    [InlineData("Manager", "User", false)]
    [InlineData("Manager", "Admin", false)]
    [InlineData("Admin", "User", false)]
    [InlineData("Admin", "Manager", true)]
    [InlineData("Admin,User", "Manager", false)]
    [InlineData("", "User", false)]
    [InlineData("Admin,User", "User", false)]
    [InlineData("User", "User", false)]
    [InlineData("User", "manager", false)]
    public async Task ValidRole_ReplacesCompleteMembershipAndInvalidatesSessions(string originalRoles, string role, bool protect)
    {
        await using var browser = await StartAsync();
        var target = await browser.UserAsync(originalRoles, protect);
        await browser.DependentsAsync(target);
        var before = await ReadAsync(target.Id);
        using var result = await EditAsync(browser, target, role);
        var after = await ReadAsync(target.Id);
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
        Assert.Equal("/Admin/Users", result.Headers.Location!.OriginalString);
        Assert.Equal(new[] { role == "manager" ? "Manager" : role }, after.Roles);
        AssertChanged(before, after, stampChanged: true);
        Assert.Equal(before.Audits + 1, after.Audits);
    }

    /// <summary>The default username refuses all edits, and username changes are independently rejected.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExistingRestrictions_RetainDefaultAdminAndUsername(bool defaultAdmin, bool rename)
    {
        await using var browser = await StartAsync();
        var target = await browser.UserAsync("Admin", protect: true, name: defaultAdmin ? "admin" : null);
        var before = await ReadAsync(target.Id);
        using var result = await EditAsync(browser, target, "User", rename ? "changed-name" : null);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.True(before == await ReadAsync(target.Id));
        if (defaultAdmin)
        {
            using var renamed = await EditAsync(browser, target, "Manager", "changed-name");
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
            Assert.True(before == await ReadAsync(target.Id));
        }
    }

    /// <summary>Actual protected forms retain required fields; ambiguous memberships require a deliberate choice.</summary>
    [Theory]
    [InlineData("Manager")]
    [InlineData("Admin,User")]
    [InlineData("")]
    public async Task ProtectedRenderedForm_RetainsFieldsAndRequiresExplicitRole(string roles)
    {
        await using var browser = await StartAsync();
        var target = await browser.UserAsync(roles, protect: true);
        var before = await ReadAsync(target.Id);
        using var get = await browser.Client.GetAsync($"/Admin/Users/Edit/{target.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var doc = await new HtmlParser().ParseDocumentAsync(await get.Content.ReadAsStringAsync());
        Assert.Equal(target.DisplayName, doc.QuerySelector("[name=DisplayName]")!.GetAttribute("value"));
        Assert.True(doc.QuerySelector("[name=DisplayName]")!.HasAttribute("readonly"));
        var fields = doc.QuerySelectorAll("form input[name], form select[name]")
            .Where(e => e.GetAttribute("type") != "checkbox")
            .GroupBy(e => e.GetAttribute("name")!)
            .ToDictionary(g => g.Key, g => g.First().GetAttribute("value")
                ?? g.First().QuerySelector("option[selected]")?.GetAttribute("value")
                ?? g.First().QuerySelector("option")?.GetAttribute("value") ?? "");
        Assert.Equal(roles == "Manager" ? "Manager" : "", fields["Role"]);
        if (roles != "Manager")
        {
            Assert.NotNull(doc.QuerySelector("select[name=Role]"));
            using var rejected = await browser.PostAsync($"/Admin/Users/Edit/{target.Id}", fields.Select(f => (f.Key, f.Value)).ToArray());
            Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
            Assert.True(before == await ReadAsync(target.Id));
            fields["Role"] = "User";
        }
        // Both toggles remain permitted, including explicit unprotection via the rendered form.
        fields["IsActive"] = "false";
        fields["IsProtected"] = "false";
        using var post = await browser.PostAsync($"/Admin/Users/Edit/{target.Id}", fields.Select(f => (f.Key, f.Value)).ToArray());
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        var after = await ReadAsync(target.Id);
        Assert.Equal(new[] { roles == "Manager" ? "Manager" : "User" }, after.Roles);
        Assert.Equal(target.DisplayName, after.DisplayName);
        Assert.False(after.Active);
        Assert.False(after.Protected);
        Assert.NotEqual(before.Security, after.Security);
        Assert.True(before.Other == after.Other && before.Dependents == after.Dependents);
        Assert.Equal(before.Audits + 1, after.Audits);
    }

    /// <summary>Real authentication and genuine antiforgery stay authoritative before any account write.</summary>
    [Theory]
    [InlineData("", true)]
    [InlineData("Manager", true)]
    [InlineData("User", true)]
    [InlineData("Admin", false)]
    public async Task Admission_RetainsAuthenticationAndAntiforgery(string actor, bool antiforgery)
    {
        await using var browser = await StartAsync(actor);
        var target = await browser.UserAsync("User");
        var before = await ReadAsync(target.Id);
        using var result = await browser.PostAsync("/Admin/Users/Edit", antiforgery,
            ("Id", target.Id), ("UserName", target.UserName!), ("DisplayName", "Changed"), ("Role", "Admin"));
        Assert.Equal(antiforgery ? HttpStatusCode.Redirect : HttpStatusCode.BadRequest, result.StatusCode);
        if (antiforgery) Assert.Contains(actor == "" ? "/Login" : "/AccessDenied", result.Headers.Location!.OriginalString);
        Assert.True(before == await ReadAsync(target.Id));
    }

    /// <summary>Every checked Identity failure and intermediate exception rolls back fields, roles and stamps.</summary>
    [Theory]
    [InlineData(1, "concurrency")]
    [InlineData(2, "concurrency")]
    [InlineData(3, "concurrency")]
    [InlineData(4, "concurrency")]
    [InlineData(2, "persistence")]
    [InlineData(3, "cancel")]
    public async Task FailedIdentitySave_RollsBackCompleteEdit(int save, string failure)
    {
        var fault = new SaveFault(save, failure);
        await using var browser = await StartAsync("Admin", fault);
        var target = await browser.UserAsync("User", protect: true);
        await browser.DependentsAsync(target);
        var before = await ReadAsync(target.Id);
        fault.TargetId = target.Id;
        using var result = await EditAsync(browser, target, "Manager");
        Assert.True(fault.Fired);
        Assert.Equal(failure == "concurrency" ? HttpStatusCode.OK : HttpStatusCode.InternalServerError, result.StatusCode);
        Assert.True(before == await ReadAsync(target.Id));
    }

    /// <summary>Cancellation at commit rolls back the already saved synchronous success audit as well as Identity writes.</summary>
    [Fact]
    public async Task CancelledCommit_RollsBackAuditAndAccount()
    {
        var fault = new CancelCommit();
        await using var browser = await StartAsync("Admin", fault);
        var target = await browser.UserAsync("User", protect: true);
        await browser.DependentsAsync(target);
        var before = await ReadAsync(target.Id);
        fault.Armed = true;
        using var result = await EditAsync(browser, target, "Manager");
        Assert.True(fault.Fired);
        Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode);
        Assert.True(before == await ReadAsync(target.Id));
    }

    /// <summary>Real PostgreSQL role, stamp and synchronous audit failures undo all earlier Identity saves.</summary>
    [Theory]
    [InlineData("role")]
    [InlineData("stamp")]
    [InlineData("audit")]
    public async Task PostgreSqlFailure_RollsBackCompleteEdit(string stage)
    {
        await using var browser = await StartAsync();
        var target = await browser.UserAsync("User", protect: true);
        await browser.DependentsAsync(target);
        var before = await ReadAsync(target.Id);
        Assert.True(Guid.TryParse(target.Id, out _));
        var table = stage == "role" ? "AspNetUserRoles" : stage == "stamp" ? "AspNetUsers" : "AuditLogs";
        var predicate = stage == "role" ? $"NEW.\"UserId\" = '{target.Id}'"
            : stage == "stamp" ? $"NEW.\"Id\" = '{target.Id}' AND NEW.\"SecurityStamp\" IS DISTINCT FROM OLD.\"SecurityStamp\""
            : "NEW.\"Action\" = 'Edit User'";
        await using var setup = fixture.CreateContext();
        await setup.Database.ExecuteSqlRawAsync($$"""
            CREATE FUNCTION public.reject_admin_edit() RETURNS trigger LANGUAGE plpgsql AS
            $body$ BEGIN RAISE EXCEPTION 'Fixture-owned PostgreSQL failure'; END $body$;
            CREATE TRIGGER admin_edit_failure BEFORE {{(stage == "stamp" ? "UPDATE" : "INSERT")}} ON "{{table}}"
            FOR EACH ROW WHEN ({{predicate}}) EXECUTE FUNCTION public.reject_admin_edit();
            """);
        try
        {
            using var result = await EditAsync(browser, target, "Manager");
            Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode);
            Assert.True(before == await ReadAsync(target.Id));
        }
        finally
        {
            await setup.Database.ExecuteSqlRawAsync($"DROP TRIGGER admin_edit_failure ON \"{table}\"; DROP FUNCTION public.reject_admin_edit();");
        }
    }

    /// <summary>The Admin transaction retains Identity's parent-row lock until promotion commits, then Manager denies.</summary>
    [Fact]
    public async Task InFlightAdminPromotion_BlocksManagerTokenUntilCommittedRolesAreVisible()
    {
        var pause = new PauseAfterAccountSave();
        await using var admin = await StartAsync("Admin", pause);
        await using var manager = await StartAsync("Manager");
        var target = await admin.UserAsync("User");
        await admin.DependentsAsync(target);
        var before = await ReadAsync(target.Id);
        pause.TargetId = target.Id;
        var promotion = EditAsync(admin, target, "Admin");
        Task<HttpResponseMessage>? token = null;
        try
        {
            await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            token = manager.PostAsync("ApiToken/Create", ("id", target.Id), ("name", ApiTokenService.ConnectionTokenName));
            await WaitForBlockedManagerAsync(pause.BackendPid);
            Assert.False(token.IsCompleted);
            Assert.True(before == await ReadAsync(target.Id));
            pause.Release.TrySetResult();
            using var promoted = await promotion.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(HttpStatusCode.Redirect, promoted.StatusCode);
            var afterPromotion = await ReadAsync(target.Id);
            using var denied = await token.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
            Assert.Contains("AccessDenied", denied.Headers.Location!.OriginalString);
            Assert.Equal(new[] { "Admin" }, afterPromotion.Roles);
            Assert.True(afterPromotion == await ReadAsync(target.Id));
            await using var fresh = fixture.CreateContext();
            Assert.False(await fresh.ApiTokens.AnyAsync(t => t.UserId == target.Id && t.Name == ApiTokenService.ConnectionTokenName));
        }
        finally
        {
            pause.Release.TrySetResult();
            await DrainAsync(promotion);
            if (token != null) await DrainAsync(token);
        }
    }

    /// <summary>Attributes a real PostgreSQL account-lock wait to the paused Admin connection.</summary>
    private async Task WaitForBlockedManagerAsync(int adminPid)
    {
        await using var observer = new NpgsqlConnection(fixture.ConnectionString);
        await observer.OpenAsync();
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                WHERE datname = current_database() AND wait_event_type = 'Lock'
                AND @admin = ANY(pg_blocking_pids(pid)) AND query LIKE '%FOR UPDATE%')
                """, observer);
            command.Parameters.AddWithValue("admin", adminPid);
            if ((bool)(await command.ExecuteScalarAsync())!) return;
            await Task.Delay(50);
        }
        Assert.Fail("Manager token request never waited on the Admin Identity transaction.");
    }

    /// <summary>Drains owned requests after releasing their gate while preserving the primary assertion failure.</summary>
    private static async Task DrainAsync(Task<HttpResponseMessage> pending)
    {
        try { using var response = await pending.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch { /* Host and fixture cleanup still run after a primary failure. */ }
    }

    /// <summary>Reads every account scalar, credentials, roles and dependent keys from a fresh context.</summary>
    private async Task<State> ReadAsync(string id)
    {
        await using var db = fixture.CreateContext();
        var user = await db.Users.SingleAsync(u => u.Id == id);
        var roles = await (from membership in db.UserRoles join role in db.Roles on membership.RoleId equals role.Id
            where membership.UserId == id orderby role.Name select role.Name!).ToArrayAsync();
        var unchanged = db.Entry(user).Properties.Where(p => p.Metadata.Name is not
            ("DisplayName" or "IsActive" or "IsProtected" or "ConcurrencyStamp" or "SecurityStamp"))
            .ToDictionary(p => p.Metadata.Name, p => p.CurrentValue);
        var dependents = JsonSerializer.Serialize(new
        {
            Tokens = await db.ApiTokens.Where(t => t.UserId == id).OrderBy(t => t.Id)
                .Select(t => new { t.Id, t.UserId, t.Name, t.Token, t.TokenHash, t.CreatedAt }).ToArrayAsync(),
            Trips = await db.Trips.Where(t => t.UserId == id).Select(t => t.Id).ToArrayAsync(),
            Locations = await db.Locations.Where(t => t.UserId == id).Select(t => t.Id).ToArrayAsync(),
            Groups = await db.Groups.Where(t => t.OwnerUserId == id).Select(t => t.Id).ToArrayAsync(),
            Members = await db.GroupMembers.Where(t => t.UserId == id).Select(t => t.Id).ToArrayAsync(),
            Claims = await db.UserClaims.Where(t => t.UserId == id).Select(t => t.Id).ToArrayAsync(),
            Logins = await db.UserLogins.Where(t => t.UserId == id).Select(t => t.LoginProvider).ToArrayAsync()
        });
        Assert.Equal(PasswordVerificationResult.Success, new PasswordHasher<ApplicationUser>()
            .VerifyHashedPassword(user, user.PasswordHash!, "Disposable-Fixture-Password1!"));
        return new State(user.DisplayName, user.IsActive, user.IsProtected, user.SecurityStamp, user.ConcurrencyStamp,
            JsonSerializer.Serialize(roles), JsonSerializer.Serialize(unchanged), dependents,
            await db.AuditLogs.CountAsync(a => a.Action == "Edit User" && a.Details.Contains(user.UserName!)));
    }

    /// <summary>Asserts committed fields and session invalidation while retaining credentials and dependent state.</summary>
    private static void AssertChanged(State before, State after, bool stampChanged)
    {
        Assert.Equal("Changed", after.DisplayName);
        Assert.False(after.Active);
        Assert.False(after.Protected);
        Assert.NotEqual(before.Concurrency, after.Concurrency);
        Assert.Equal(stampChanged, before.Security != after.Security);
        Assert.True(before.Other == after.Other);
        Assert.True(before.Dependents == after.Dependents);
    }

    /// <summary>Attaches production routes and real password sign-in using the existing helper.</summary>
    private Task<Browser> StartAsync(string actor = "Admin", params IInterceptor[] interceptors) =>
        Browser.StartAsync(fixture, CreateTestDirectory(), actor, interceptors);

    /// <summary>Omits a role key completely when null, and otherwise uses the actual form binder.</summary>
    private static Task<HttpResponseMessage> EditAsync(Browser browser, ApplicationUser target, string? role, string? username = null)
    {
        var fields = new List<(string, string)> { ("Id", target.Id), ("UserName", username ?? target.UserName!),
            ("DisplayName", "Changed"), ("IsActive", "false"), ("IsProtected", "false") };
        if (role != null) fields.Add(("Role", role));
        return browser.PostAsync("/Admin/Users/Edit", fields.ToArray());
    }

    /// <summary>Immutable private snapshot supports value equality without publishing credential material.</summary>
    private sealed record State(string DisplayName, bool Active, bool Protected, string? Security,
        string? Concurrency, string RoleJson, string Other, string Dependents, int Audits)
    {
        internal string[] Roles => JsonSerializer.Deserialize<string[]>(RoleJson)!;
    }

    /// <summary>Injects a checked real-store concurrency result or an exception at a chosen account save.</summary>
    private sealed class SaveFault(int atSave, string failure) : SaveChangesInterceptor
    {
        internal string? TargetId { get; set; }
        internal bool Fired { get; private set; }
        private int _saves;

        /// <summary>Leaves the real store intact until removal, addition, update or stamp persistence.</summary>
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (TargetId == null || Fired || !data.Context!.ChangeTracker.Entries<ApplicationUser>()
                    .Any(e => e.Entity.Id == TargetId && e.State == EntityState.Modified) || ++_saves != atSave)
                return ValueTask.FromResult(result);
            Fired = true;
            if (failure == "cancel") throw new OperationCanceledException("Fixture-owned cancellation before commit.");
            if (failure == "persistence") throw new DbUpdateException("Fixture-owned persistence failure.");
            throw new DbUpdateConcurrencyException("Fixture-owned Identity concurrency failure.");
        }
    }

    /// <summary>Pauses after the first real Identity save without altering its SQL or result.</summary>
    private sealed class PauseAfterAccountSave : SaveChangesInterceptor
    {
        internal string? TargetId { get; set; }
        internal int BackendPid { get; private set; }
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Holds the scheduling gate while the outer transaction retains the parent account lock.</summary>
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result,
            CancellationToken cancellationToken = default)
        {
            if (TargetId == null || !data.Context!.ChangeTracker.Entries<ApplicationUser>().Any(e => e.Entity.Id == TargetId)) return result;
            TargetId = null;
            Assert.NotNull(data.Context.Database.CurrentTransaction);
            BackendPid = ((NpgsqlConnection)data.Context.Database.GetDbConnection()).ProcessID;
            Reached.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            return result;
        }
    }

    /// <summary>Exercises disposal after every save, including LogAudit's synchronous shared-context save.</summary>
    private sealed class CancelCommit : DbTransactionInterceptor
    {
        internal bool Armed { get; set; }
        internal bool Fired { get; private set; }

        /// <summary>Stops the real commit only after observing its saved success audit inside the transaction.</summary>
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData data, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result;
            Armed = false;
            Fired = true;
            Assert.True(await data.Context!.Set<AuditLog>().AnyAsync(a => a.Action == "Edit User", cancellationToken));
            throw new OperationCanceledException("Fixture-owned cancellation at commit.");
        }
    }
}
