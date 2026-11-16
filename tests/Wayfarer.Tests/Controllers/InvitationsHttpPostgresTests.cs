using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Middleware;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Exercises invitation reads through real Identity/MVC and host-owned request cancellation.</summary>
[Collection("PostgreSQL import reconciliation")]
public sealed class InvitationsHttpPostgresTests(PostgresImportTestFixture fixture) : TestBase
{
    /// <summary>Aborted navigation escapes controller error handling; internal cancellation and storage errors retain it.</summary>
    [PostgresTheory]
    [InlineData("abort")]
    [InlineData("internal")]
    [InlineData("storage")]
    public async Task List_PreservesRequestAbortAndOrdinaryFailures(string failure)
    {
        var user = await fixture.CreateUserAsync();
        var fault = new InvitationReadFailure(failure);
        await using var db = fixture.CreateContext(fault);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var logs = new TestLogProvider();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(),
            services => services.AddLogging(logging => logging.AddProvider(logs)),
            host => host.UseMiddleware<ApiErrorResponseMiddleware>(),
            configureAuthorized: host => host.Use((context, next) =>
            {
                if (failure == "abort") context.RequestAborted = cancellation.Token;
                return next(context);
            }));
        using var client = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(client, app.Services, user.Id);
        using var response = await client.GetAsync("/api/invitations");
        var body = await response.Content.ReadAsStringAsync();
        var warnings = logs.Entries.Where(entry => entry.Category == typeof(InvitationsController).FullName).ToArray();
        Assert.DoesNotContain(logs.Entries, entry => entry.Category == typeof(ApiErrorResponseMiddleware).FullName);
        if (failure == "abort")
        {
            Assert.Equal((HttpStatusCode)StatusCodes.Status499ClientClosedRequest, response.StatusCode);
            Assert.Empty(body);
            Assert.Empty(warnings);
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("Failed to load invitations.", json.RootElement.GetProperty("message").GetString());
            var warning = Assert.Single(warnings);
            Assert.Equal(LogLevel.Warning, warning.Level);
            Assert.Same(fault.Exception, warning.Exception);
        }
    }

    /// <summary>An authenticated caller still receives only their pending invitations with group and inviter details.</summary>
    [PostgresFact]
    public async Task List_ReturnsAuthenticatedPendingInvitations()
    {
        var owner = await fixture.CreateUserAsync();
        var recipient = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        db.AttachRange(owner, recipient);
        var group = TestDataFixtures.CreateGroup(owner, name: "Invitation read");
        var invitation = TestDataFixtures.CreateGroupInvitation(group, owner, recipient);
        db.Groups.Add(group);
        db.GroupInvitations.AddRange(invitation, TestDataFixtures.CreateGroupInvitation(group, owner));
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(),
            configureApp: host => host.UseMiddleware<ApiErrorResponseMiddleware>());
        using var client = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(client, app.Services, recipient.Id);
        using var response = await client.GetAsync("/api/invitations");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal(invitation.Id, item.GetProperty("id").GetGuid());
        Assert.Equal(group.Name, item.GetProperty("groupName").GetString());
        Assert.Equal(owner.UserName, item.GetProperty("inviterUserName").GetString());
        Assert.Equal(recipient.Id, item.GetProperty("inviteeUserId").GetString());
    }

    /// <summary>Fails only the invitation SELECT, after genuine cookie/account authentication has completed.</summary>
    private sealed class InvitationReadFailure(string failure) : DbCommandInterceptor
    {
        internal Exception Exception { get; } = failure == "internal"
            ? new OperationCanceledException(new CancellationToken(true)) : new InvalidOperationException("Invitation storage failure");

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (failure != "abort" && command.CommandText.Contains("FROM \"GroupInvitations\"")) throw Exception;
            return ValueTask.FromResult(result);
        }
    }
}
