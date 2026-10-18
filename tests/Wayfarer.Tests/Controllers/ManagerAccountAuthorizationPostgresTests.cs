using System.Net;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Production Manager routes, real Identity and fresh postconditions in an independently owned PostgreSQL database.</summary>
public sealed class ManagerAccountAuthorizationPostgresTests(PostgresMigrationTestFixture fixture)
    : TestBase, IClassFixture<PostgresMigrationTestFixture>
{
    /// <summary>Every existing-account surface denies the entire ineligible role set before disclosure or mutation.</summary>
    [Theory]
    [InlineData("Admin", false)]
    [InlineData("Manager", false)]
    [InlineData("Admin,User", false)]
    [InlineData("Manager,User", false)]
    [InlineData("", false)]
    [InlineData("Unknown", false)]
    [InlineData("User,Unknown", false)]
    [InlineData("Admin", true)]
    [InlineData("Admin", false, true)]
    public async Task IneligibleTargets_DenyAllActionsAndRetainAccountAndDependents(string roles, bool defaultAdmin, bool protectedAdmin = false)
    {
        await using var browser = await StartAsync();
        var target = await browser.UserAsync(roles, protect: defaultAdmin || protectedAdmin, name: defaultAdmin ? "admin" : null);
        await browser.DependentsAsync(target);
        var token = await browser.TokenAsync(target, ApiTokenService.ConnectionTokenName);
        var before = await SnapshotAsync(target.Id);

        foreach (var action in new[] { "Edit", "Delete", "ChangePassword" })
        {
            using var get = await browser.Client.GetAsync($"/Manager/Users/{action}/{target.Id}");
            AssertDenied(get);
            Assert.DoesNotContain(target.UserName!, await get.Content.ReadAsStringAsync());
        }
        using var tokens = await browser.Client.GetAsync($"/Manager/ApiToken/Index/{target.Id}");
        AssertDenied(tokens);
        using var edit = await browser.EditAsync(target);
        AssertDenied(edit);
        using var delete = await browser.PostAsync("Users/DeleteConfirmed", ("id", target.Id));
        AssertDenied(delete);
        using var password = await browser.PasswordAsync(target);
        AssertDenied(password);
        using var create = await browser.PostAsync("ApiToken/Create", ("id", target.Id), ("name", "forbidden"));
        AssertDenied(create);
        using var regenerate = await browser.PostAsync("ApiToken/Regenerate", ("id", target.Id), ("name", token.Name));
        AssertDenied(regenerate);
        using var deleteToken = await browser.PostAsync("ApiToken/Delete", ("id", target.Id), ("tokenId", token.Id.ToString()));
        AssertDenied(deleteToken);
        using var index = await browser.Client.GetAsync($"/Manager/Users?search={target.UserName}");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        var document = await new HtmlParser().ParseDocumentAsync(await index.Content.ReadAsStringAsync());
        Assert.DoesNotContain(target.UserName!, document.QuerySelector("tbody")!.TextContent);
        Assert.Equal(before, await SnapshotAsync(target.Id));
    }

    /// <summary>Invalid replacement roles and usernames preserve fields, memberships, credentials and stamp.</summary>
    [Theory]
    [InlineData("Unknown", false)]
    [InlineData("Admin", false)]
    [InlineData("Manager", false)]
    [InlineData("", false)]
    [InlineData("User", true)]
    public async Task InvalidEdit_LeavesAllPersistedStateUnchanged(string role, bool changeUsername)
    {
        await using var browser = await StartAsync();
        var target = await browser.UserAsync("User", protect: true);
        var before = await SnapshotAsync(target.Id);
        using var response = await browser.EditAsync(target, role, changeUsername ? "changed-name" : target.UserName);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(target.Id));
    }

    /// <summary>Allowed inactive/protected targets retain edit, password and token workflows; protected deletion refuses.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task UserOnlyTargets_KeepLegitimateOperations(bool active, bool protect)
    {
        await using var browser = await StartAsync();
        var target = await browser.UserAsync("User", protect, active);
        // Group authority is independent of installation roles, including an Owner membership.
        await browser.DependentsAsync(target);
        foreach (var action in new[] { "Edit", "Delete", "ChangePassword" })
        {
            using var form = await browser.Client.GetAsync($"/Manager/Users/{action}/{target.Id}");
            Assert.Equal(HttpStatusCode.OK, form.StatusCode);
        }
        using var index = await browser.Client.GetAsync($"/Manager/Users?search={target.UserName}");
        Assert.Contains(target.UserName!, await index.Content.ReadAsStringAsync());
        if (protect)
        {
            var before = await SnapshotAsync(target.Id);
            using var refused = await browser.PostAsync("Users/DeleteConfirmed", ("id", target.Id));
            Assert.EndsWith($"/Manager/Users/Delete/{target.Id}", refused.Headers.Location!.OriginalString);
            Assert.Equal(before, await SnapshotAsync(target.Id));
        }
        var oldHash = target.PasswordHash;
        var passwordStamp = target.SecurityStamp;
        using var password = await browser.PasswordAsync(target);
        Assert.Equal(HttpStatusCode.Redirect, password.StatusCode);
        await using (var fresh = fixture.CreateContext())
        {
            var updated = await fresh.Users.SingleAsync(u => u.Id == target.Id);
            Assert.NotEqual(oldHash, updated.PasswordHash);
            Assert.NotEqual(passwordStamp, updated.SecurityStamp);
            Assert.Equal(PasswordVerificationResult.Success, new PasswordHasher<ApplicationUser>()
                .VerifyHashedPassword(updated, updated.PasswordHash!, Browser.ReplacementPassword));
        }
        await AssertAllowedTokensAsync(browser, target);

        // Submit the actual rendered form, including hidden values and checkbox ordering.
        using var editForm = await browser.Client.GetAsync($"/Manager/Users/Edit/{target.Id}");
        var document = await new HtmlParser().ParseDocumentAsync(await editForm.Content.ReadAsStringAsync());
        var fields = document.QuerySelectorAll("input[name], select[name]")
            .Where(e => e.GetAttribute("type") != "checkbox")
            .GroupBy(e => e.GetAttribute("name")!)
            .ToDictionary(g => g.Key, g => g.First().GetAttribute("value") ?? g.First().QuerySelector("option")?.GetAttribute("value") ?? "");
        Assert.Equal("User", fields["Role"]);
        Assert.Equal(target.DisplayName, fields["DisplayName"]);
        var expectedDisplayName = protect ? target.DisplayName : "Changed";
        fields["DisplayName"] = expectedDisplayName;
        fields["IsProtected"] = "false";
        fields["IsActive"] = "false";
        var oldStamp = await ReadStampAsync(target.Id);
        using var edit = await browser.PostAsync("Users/Edit/" + target.Id, fields.Select(f => (f.Key, f.Value)).ToArray());
        Assert.Equal(HttpStatusCode.Redirect, edit.StatusCode);
        await using (var fresh = fixture.CreateContext())
        {
            var updated = await fresh.Users.SingleAsync(u => u.Id == target.Id);
            Assert.Equal(expectedDisplayName, updated.DisplayName);
            Assert.False(updated.IsProtected);
            Assert.False(updated.IsActive);
            Assert.NotEqual(oldStamp, updated.SecurityStamp);
            Assert.Equal(new[] { "User" }, await RolesAsync(fresh, target.Id));
        }
        using var delete = await browser.PostAsync("Users/DeleteConfirmed", ("id", target.Id));
        Assert.Equal(HttpStatusCode.Redirect, delete.StatusCode);
        await using var final = fixture.CreateContext();
        Assert.False(await final.Users.AnyAsync(u => u.Id == target.Id));
        Assert.Empty(await final.ApiTokens.Where(t => t.UserId == target.Id).ToListAsync());
        Assert.Empty(await final.Trips.Where(t => t.UserId == target.Id).ToListAsync());
    }

    /// <summary>Authentication and all six mutation antiforgery filters continue rejecting requests before persistence.</summary>
    [Theory]
    [InlineData("", true)]
    [InlineData("User", true)]
    [InlineData("Admin", true)]
    [InlineData("Manager", false)]
    [InlineData("Manager", false, true)]
    public async Task CallerAndAntiforgeryGuards_PreserveState(string callerRole, bool antiforgery, bool invalidAntiforgery = false)
    {
        await using var browser = await StartAsync(callerRole);
        var target = await browser.UserAsync("User");
        var token = await browser.TokenAsync(target, "existing");
        var before = await SnapshotAsync(target.Id);
        foreach (var action in new[] { "Users/Edit", "Users/DeleteConfirmed", "Users/ChangePassword", "ApiToken/Create", "ApiToken/Regenerate", "ApiToken/Delete" })
        {
            using var response = await browser.PostAsync(action, antiforgery,
                ("id", target.Id), ("UserId", target.Id), ("UserName", target.UserName!),
                ("DisplayName", "changed"), ("Role", "User"), ("name", "existing"), ("tokenId", token.Id.ToString()),
                ("__RequestVerificationToken", invalidAntiforgery ? "invalid" : ""),
                ("NewPassword", Browser.ReplacementPassword), ("ConfirmPassword", Browser.ReplacementPassword));
            if (callerRole == "Manager") Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            else
            {
                Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
                Assert.Contains(callerRole == "" ? "/Login" : "/AccessDenied", response.Headers.Location!.OriginalString);
            }
        }
        Assert.Equal(before, await SnapshotAsync(target.Id));
    }

    /// <summary>Missing accounts retain the existing not-found/redirect behavior without creating credentials.</summary>
    [Fact]
    public async Task MissingTargets_KeepExistingResponses()
    {
        await using var browser = await StartAsync();
        var missing = new ApplicationUser { Id = "missing", UserName = "missing", DisplayName = "missing" };
        foreach (var action in new[] { "Edit", "Delete", "ChangePassword" })
        {
            using var get = await browser.Client.GetAsync($"/Manager/Users/{action}/missing");
            Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        }
        using var edit = await browser.EditAsync(missing);
        using var password = await browser.PasswordAsync(missing);
        using var delete = await browser.PostAsync("Users/DeleteConfirmed", ("id", missing.Id));
        Assert.All(new[] { edit, password, delete }, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        using var index = await browser.Client.GetAsync("/Manager/ApiToken/Index/missing");
        Assert.Equal("/Manager", index.Headers.Location!.OriginalString);
        foreach (var action in new[] { "Create", "Regenerate", "Delete" })
        {
            using var response = await browser.PostAsync("ApiToken/" + action, ("id", missing.Id), ("name", "missing"), ("tokenId", "-1"));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Manager/ApiToken", response.Headers.Location!.OriginalString);
        }
    }

    /// <summary>Token mutations preserve duplicate names, owner matching, canonical deletion and secret-free HTML.</summary>
    private async Task AssertAllowedTokensAsync(Browser browser, ApplicationUser target)
    {
        var canonical = await browser.TokenAsync(target, ApiTokenService.ConnectionTokenName);
        var other = await browser.UserAsync("User");
        using var create = await browser.PostAsync("ApiToken/Create", ("id", target.Id), ("name", "created"));
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        using var duplicate = await browser.PostAsync("ApiToken/Create", ("id", target.Id), ("name", "created"));
        await using var fresh = fixture.CreateContext();
        var created = await fresh.ApiTokens.AsNoTracking().SingleAsync(t => t.UserId == target.Id && t.Name == "created");
        Assert.Null(created.Token);
        Assert.False(string.IsNullOrWhiteSpace(created.TokenHash));
        using var mismatch = await browser.PostAsync("ApiToken/Delete", ("id", other.Id), ("tokenId", created.Id.ToString()));
        Assert.True(await fresh.ApiTokens.AnyAsync(t => t.Id == created.Id));
        using var forbidden = await browser.PostAsync("ApiToken/Delete", ("id", target.Id), ("tokenId", canonical.Id.ToString()));
        Assert.True(await fresh.ApiTokens.AnyAsync(t => t.Id == canonical.Id));
        var canonicalHash = canonical.TokenHash;
        using var regenerate = await browser.PostAsync("ApiToken/Regenerate", ("id", target.Id), ("name", canonical.Name));
        var rotated = await fresh.ApiTokens.AsNoTracking().SingleAsync(t => t.Id == canonical.Id);
        Assert.NotEqual(canonicalHash, rotated.TokenHash);
        Assert.Null(rotated.Token);
        using var list = await browser.Client.GetAsync($"/Manager/ApiToken/Index/{target.Id}");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var html = await list.Content.ReadAsStringAsync();
        Assert.Contains("created", html);
        Assert.DoesNotContain(created.TokenHash!, html);
        Assert.DoesNotContain(rotated.TokenHash!, html);
        using var delete = await browser.PostAsync("ApiToken/Delete", ("id", target.Id), ("tokenId", created.Id.ToString()));
        Assert.False(await fresh.ApiTokens.AnyAsync(t => t.Id == created.Id));
    }

    /// <summary>Captures complete persisted account/token rows and dependent identifiers with a new context.</summary>
    private async Task<string> SnapshotAsync(string id)
    {
        await using var fresh = fixture.CreateContext();
        var user = await fresh.Users.SingleAsync(u => u.Id == id);
        return JsonSerializer.Serialize(new
        {
            User = fresh.Entry(user).Properties.ToDictionary(p => p.Metadata.Name, p => p.CurrentValue),
            Roles = await RolesAsync(fresh, id),
            Tokens = await fresh.ApiTokens.Where(t => t.UserId == id).OrderBy(t => t.Id)
                .Select(t => new { t.Id, t.UserId, t.Name, t.Token, t.TokenHash, t.CreatedAt }).ToArrayAsync(),
            Trips = await fresh.Trips.Where(t => t.UserId == id).Select(t => t.Id).ToArrayAsync(),
            Locations = await fresh.Locations.Where(t => t.UserId == id).Select(t => t.Id).ToArrayAsync(),
            Groups = await fresh.Groups.Where(t => t.OwnerUserId == id).Select(t => t.Id).ToArrayAsync(),
            Memberships = await fresh.GroupMembers.Where(t => t.UserId == id).Select(t => t.Id).ToArrayAsync()
        });
    }

    /// <summary>Reads current stamp independently of the request host's tracked entities.</summary>
    private async Task<string?> ReadStampAsync(string id)
    {
        await using var fresh = fixture.CreateContext();
        return await fresh.Users.Where(u => u.Id == id).Select(u => u.SecurityStamp).SingleAsync();
    }

    /// <summary>Identity-cookie forbids redirect to the existing access-denied page.</summary>
    private static void AssertDenied(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/AccessDenied", response.Headers.Location!.OriginalString);
    }

    /// <summary>Reads all persisted installation roles without depending on Identity's returned order.</summary>
    private static Task<string[]> RolesAsync(ApplicationDbContext db, string id) =>
        (from membership in db.UserRoles join role in db.Roles on membership.RoleId equals role.Id
         where membership.UserId == id orderby role.Name select role.Name!).ToArrayAsync();

    /// <summary>Attaches the established disposable relational fixture and test-owned storage root.</summary>
    private Task<Browser> StartAsync(string callerRole = "Manager") => Browser.StartAsync(fixture, CreateTestDirectory(), callerRole);

    /// <summary>Local browser transport for actual password sign-in and identity-bound forms; no fabricated principal.</summary>
    private sealed class Browser(ApplicationDbContext db, WebApplication app, IServiceScope scope, HttpClient client) : IAsyncDisposable
    {
        private const string Password = "Disposable-Fixture-Password1!";
        internal const string ReplacementPassword = "Replacement-Fixture-Password2!";
        private readonly Dictionary<string, string> _cookies = [];
        private string _antiforgery = "";
        internal HttpClient Client => client;
        private UserManager<ApplicationUser> Users => scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        /// <summary>Starts production routing and signs in through the real Identity login endpoint.</summary>
        internal static async Task<Browser> StartAsync(PostgresMigrationTestFixture fixture, string root, string callerRole)
        {
            fixture.RequireAvailable();
            var db = fixture.CreateContext();
            if (!await db.ApplicationSettings.AnyAsync())
            {
                db.ApplicationSettings.Add(new ApplicationSettings());
                await db.SaveChangesAsync();
            }
            var app = await IdentityRouteHost.StartAsync(db, root);
            var scope = app.Services.CreateScope();
            var client = app.GetTestClient();
            client.BaseAddress = new Uri("https://wayfarer.test");
            var browser = new Browser(db, app, scope, client);
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            foreach (var role in new[] { "User", "Manager", "Admin", "Unknown" })
                if (!await roles.RoleExistsAsync(role)) Assert.True((await roles.CreateAsync(new IdentityRole(role))).Succeeded);
            await browser.AntiforgeryAsync();
            if (callerRole != "")
            {
                var actor = await browser.UserAsync(callerRole);
                using var login = await browser.PostAsync("/Identity/Account/Login", ("Input.Username", actor.UserName!), ("Input.Password", Password));
                Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
                Assert.Contains(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Identity.Application="));
                await browser.AntiforgeryAsync();
            }
            return browser;
        }

        /// <summary>Creates a persisted target through production Identity, then optionally deactivates it.</summary>
        internal async Task<ApplicationUser> UserAsync(string roles, bool protect = false, bool active = true, string? name = null)
        {
            var user = new ApplicationUser { UserName = name ?? $"fixture-{Guid.NewGuid():N}", DisplayName = "Original", IsProtected = protect, IsActive = true };
            Assert.True((await Users.CreateAsync(user, Password)).Succeeded);
            foreach (var role in roles.Split(',', StringSplitOptions.RemoveEmptyEntries))
                Assert.True((await Users.AddToRoleAsync(user, role)).Succeeded);
            user.IsActive = active;
            Assert.True((await Users.UpdateAsync(user)).Succeeded);
            return user;
        }

        /// <summary>Seeds only hashed fixture credentials.</summary>
        internal async Task<ApiToken> TokenAsync(ApplicationUser user, string name)
        {
            var token = new ApiToken { UserId = user.Id, User = user, Name = name,
                TokenHash = ApiTokenService.HashToken(Guid.NewGuid().ToString()), CreatedAt = DateTime.UtcNow };
            db.ApiTokens.Add(token);
            await db.SaveChangesAsync();
            return token;
        }

        /// <summary>Seeds representative cascade targets and a Group Owner membership, independently of Identity roles.</summary>
        internal async Task DependentsAsync(ApplicationUser user)
        {
            await TokenAsync(user, "dependent-token");
            db.Trips.Add(new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Disposable trip", UpdatedAt = DateTime.UtcNow });
            db.Locations.Add(new Wayfarer.Models.Location { UserId = user.Id, TimeZoneId = "Etc/UTC", Coordinates = new Point(23, 38) { SRID = 4326 }, Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow });
            var group = new Group { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = "Disposable group", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Groups.Add(group);
            db.GroupMembers.Add(new GroupMember { Id = Guid.NewGuid(), GroupId = group.Id, UserId = user.Id, Role = "Owner", JoinedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        /// <summary>Submits all allowed edit fields, changing protection/activity to expose partial mutation regressions.</summary>
        internal Task<HttpResponseMessage> EditAsync(ApplicationUser user, string role = "User", string? username = null) => PostAsync("Users/Edit",
            ("Id", user.Id), ("UserName", username ?? user.UserName!), ("DisplayName", "Changed"), ("Role", role), ("IsActive", "false"), ("IsProtected", "false"));

        /// <summary>Submits a policy-valid password reset request.</summary>
        internal Task<HttpResponseMessage> PasswordAsync(ApplicationUser user) => PostAsync("Users/ChangePassword", ("UserId", user.Id),
            ("NewPassword", ReplacementPassword), ("ConfirmPassword", ReplacementPassword));

        /// <summary>Posts a form through the real binder and antiforgery filter.</summary>
        internal Task<HttpResponseMessage> PostAsync(string path, params (string Key, string Value)[] fields) => PostAsync(path, true, fields);

        /// <summary>Allows explicit missing-antiforgery cases while retaining genuine browser cookies.</summary>
        internal async Task<HttpResponseMessage> PostAsync(string path, bool antiforgery, params (string Key, string Value)[] fields)
        {
            var values = fields.ToDictionary(f => f.Key, f => f.Value);
            if (antiforgery) values["__RequestVerificationToken"] = _antiforgery;
            var response = await client.PostAsync(path.StartsWith('/') ? path : "/Manager/" + path, new FormUrlEncodedContent(values));
            Cookies(response);
            return response;
        }

        /// <summary>Requests a fresh identity-bound token from the established host helper endpoint.</summary>
        private async Task AntiforgeryAsync()
        {
            using var response = await client.GetAsync("/test-antiforgery");
            response.EnsureSuccessStatusCode();
            Cookies(response);
            _antiforgery = await response.Content.ReadAsStringAsync();
        }

        /// <summary>Retains framework-issued cookies, without replacing authentication claims.</summary>
        private void Cookies(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("Set-Cookie", out var headers))
                foreach (var header in headers)
                {
                    var pair = header.Split(';')[0].Split('=', 2);
                    _cookies[pair[0]] = pair[1];
                }
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", _cookies.Select(pair => $"{pair.Key}={pair.Value}")));
        }

        /// <summary>Releases services, host and context; the existing fixture owns database cleanup.</summary>
        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            scope.Dispose();
            await app.DisposeAsync();
            await db.DisposeAsync();
        }
    }
}
