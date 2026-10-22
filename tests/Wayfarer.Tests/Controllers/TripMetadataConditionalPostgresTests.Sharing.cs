using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class TripMetadataConditionalPostgresTests
{
    /// <summary>Neither sharing direction may use cookie or tracked-account authority after a confirmed Trip lock wait.</summary>
    [PostgresTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task SharingLockWait_DeactivatedCookieCannotWrite(bool editorRoute, bool enabled)
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        await db.Trips.Where(t => t.Id == trip.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ShareProgressEnabled, !enabled));
        var probe = new MutationProbe();
        await using var app = await StartAsync(db, [probe], app => app.Use(async (context, next) =>
        {
            // Exercise a stale tracked Identity entity as well as the genuine pre-deactivation cookie.
            var scoped = context.RequestServices.GetRequiredService<ApplicationDbContext>();
            await scoped.Users.SingleAsync(u => u.Id == trip.UserId);
            await next();
        }));
        using var web = await WebAsync(app, trip.UserId);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Trips\" WHERE \"Id\" = {trip.Id} FOR UPDATE");
        var pending = ShareAsync(web, trip.Id, editorRoute, enabled);
        try
        {
            await Task.WhenAny(probe.LockStarted.Task, probe.UpdateStarted.Task).WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForBlockAsync(probe.ProcessId);
            await using var authority = fixture.CreateContext();
            await authority.Users.Where(u => u.Id == trip.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
            await transaction.CommitAsync();
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            await using var verification = fixture.CreateContext();
            var stored = await verification.Trips.AsNoTracking().SingleAsync(t => t.Id == trip.Id);
            Assert.False(await verification.Users.Where(u => u.Id == trip.UserId).Select(u => u.IsActive).SingleAsync());
            output.WriteLine($"Sharing editor={editorRoute}, enabled={enabled}: HTTP {(int)response.StatusCode}; stored sharing={stored.ShareProgressEnabled}; account inactive.");
            Assert.Equal(!enabled, stored.ShareProgressEnabled);
            Assert.Equal(trip.UpdatedAt, stored.UpdatedAt);
            Assert.Equal((trip.Name, trip.Notes, trip.IsPublic), (stored.Name, stored.Notes, stored.IsPublic));
            await AssertSharingDeniedAsync(response, editorRoute, HttpStatusCode.Unauthorized);
            Assert.Empty(probe.Updates);
            using var metadata = await PatchAsync(web, trip.Id, true, new { name = trip.Name }, new { name = "Denied metadata" });
            Assert.Equal(HttpStatusCode.Unauthorized, metadata.StatusCode);
        }
        finally
        {
            await transaction.DisposeAsync();
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>The final owner predicate conceals a Trip transferred while either sharing action waits.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SharingLockWait_OwnerTransferIsConcealed(bool editorRoute)
    {
        var trip = await SeedAsync();
        var other = await fixture.CreateUserAsync();
        var probe = new MutationProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, trip.UserId);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Trips\" WHERE \"Id\" = {trip.Id} FOR UPDATE");
        var pending = ShareAsync(web, trip.Id, editorRoute, false);
        try
        {
            await probe.LockStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForBlockAsync(probe.ProcessId);
            await blocker.Trips.Where(t => t.Id == trip.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.UserId, other.Id));
            await transaction.CommitAsync();
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            await AssertSharingDeniedAsync(response, editorRoute, HttpStatusCode.NotFound);
            await using var verification = fixture.CreateContext();
            var stored = await verification.Trips.AsNoTracking().SingleAsync(t => t.Id == trip.Id);
            Assert.Equal(other.Id, stored.UserId);
            Assert.True(stored.ShareProgressEnabled);
            Assert.Equal(trip.UpdatedAt, stored.UpdatedAt);
            Assert.Empty(probe.Updates);
        }
        finally
        {
            await transaction.DisposeAsync();
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>Active owners can disable private sharing and change public sharing; private enable remains rejected.</summary>
    [PostgresTheory]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public async Task SharingActiveOwner_RespectsPublicationAndNarrowFreshness(bool editorRoute, bool isPublic, bool enabled)
    {
        var trip = await SeedAsync();
        var future = DateTime.UtcNow.AddDays(1);
        await using var db = fixture.CreateContext();
        await db.Trips.Where(t => t.Id == trip.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.IsPublic, isPublic)
            .SetProperty(t => t.ShareProgressEnabled, !enabled).SetProperty(t => t.UpdatedAt, future));
        var probe = new MutationProbe();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, trip.UserId);
        using var response = await ShareAsync(web, trip.Id, editorRoute, enabled);
        var allowed = !enabled || isPublic;
        if (!allowed) await AssertSharingDeniedAsync(response, editorRoute, HttpStatusCode.BadRequest);
        else
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("success").GetBoolean());
            Assert.Equal(enabled, editorRoute ? body.GetProperty("data").GetProperty("shareProgressEnabled").GetBoolean()
                : body.GetProperty("enabled").GetBoolean());
            var sql = Assert.Single(probe.Updates);
            Assert.Contains("\"ShareProgressEnabled\" =", sql);
            Assert.Contains("\"UpdatedAt\" =", sql);
            Assert.DoesNotContain("\"Name\" =", sql);
            Assert.DoesNotContain("\"Notes\" =", sql);
            Assert.DoesNotContain("\"IsPublic\" =", sql);
        }
        await using var verification = fixture.CreateContext();
        var stored = await verification.Trips.AsNoTracking().SingleAsync(t => t.Id == trip.Id);
        Assert.Equal(allowed ? enabled : !enabled, stored.ShareProgressEnabled);
        Assert.Equal((trip.Name, trip.Notes, isPublic), (stored.Name, stored.Notes, stored.IsPublic));
        Assert.Equal(future.Ticks / 10, stored.UpdatedAt.Ticks / 10);
    }

    /// <summary>Real role/antiforgery gates and owner confidentiality apply equally to both sharing routes.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SharingRoutes_KeepOwnershipRoleAndAntiforgery(bool editorRoute)
    {
        var trip = await SeedAsync();
        var other = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var foreign = await WebAsync(app, other.Id);
        using var nonOwner = await ShareAsync(foreign, trip.Id, editorRoute, false);
        using var missing = await ShareAsync(foreign, Guid.NewGuid(), editorRoute, false);
        await AssertSharingDeniedAsync(nonOwner, editorRoute, HttpStatusCode.NotFound);
        await AssertSharingDeniedAsync(missing, editorRoute, HttpStatusCode.NotFound);
        var foreignBody = await nonOwner.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
        var missingBody = await missing.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
        // Framework trace IDs identify requests; all resource-visible response fields must match.
        foreignBody!.Remove("traceId");
        missingBody!.Remove("traceId");
        Assert.Equal(JsonSerializer.Serialize(foreignBody), JsonSerializer.Serialize(missingBody));
        using var noAntiforgery = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(noAntiforgery, app.Services, trip.UserId);
        using var csrf = await ShareAsync(noAntiforgery, trip.Id, editorRoute, false);
        Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
        using var wrongRole = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(wrongRole, app.Services, trip.UserId, "Admin");
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(wrongRole);
        using var role = await ShareAsync(wrongRole, trip.Id, editorRoute, false);
        Assert.True(role.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect);
        if (role.StatusCode == HttpStatusCode.Redirect) Assert.Contains("AccessDenied", role.Headers.Location!.ToString());
        await using var verification = fixture.CreateContext();
        var stored = await verification.Trips.AsNoTracking().SingleAsync(t => t.Id == trip.Id);
        Assert.True(stored.ShareProgressEnabled);
        Assert.Equal(trip.UpdatedAt, stored.UpdatedAt);
    }

    /// <summary>Sends the existing sharing payload through production routing with cookie and antiforgery headers.</summary>
    private static Task<HttpResponseMessage> ShareAsync(HttpClient web, Guid tripId, bool editorRoute, bool enabled) => editorRoute
        ? web.PatchAsJsonAsync($"/api/trips/{tripId}/editor/share-progress", new { enabled })
        : web.PostAsync($"/User/Trip/ToggleShareProgress/{tripId}?enabled={enabled}", null);

    /// <summary>Preserves editor status codes and legacy JSON denial without exposing account or Trip values.</summary>
    private static async Task AssertSharingDeniedAsync(HttpResponseMessage response, bool editorRoute, HttpStatusCode status)
    {
        Assert.Equal(editorRoute ? status : HttpStatusCode.OK, response.StatusCode);
        if (!editorRoute)
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(body.GetProperty("success").GetBoolean());
            Assert.Equal(status == HttpStatusCode.BadRequest ? "Trip must be public to share progress" : "Trip not found or unauthorized",
                body.GetProperty("error").GetString());
        }
    }
}
