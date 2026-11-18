using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Models;
using Wayfarer.Util;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Verifies the routed transport and input boundaries of mobile Group commands.</summary>
public sealed class MobileGroupRequestHttpTests : TestBase
{
    /// <summary>Both empty HTTP/2 framings reach bearer and invitation checks; actual bytes fail input validation.</summary>
    [Theory]
    [InlineData("decline")]
    [InlineData("revoke")]
    public async Task InvitationCommandDistinguishesPayloadFromFraming(string command)
    {
        using var db = CreateDbContext();
        var actor = AddBearer(db);
        var group = AddGroup(db, actor, "Friends");
        db.GroupMembers.Add(new GroupMember { GroupId = group.Id, UserId = actor.Id, Role = GroupMember.Roles.Owner });
        await db.SaveChangesAsync();
        var invitation = Guid.NewGuid();
        var route = command == "decline"
            ? $"/api/mobile/invitations/{invitation}/decline"
            : $"/api/mobile/groups/{group.Id}/invitations/{invitation}/revoke";

        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(),
            configureApp: host => host.Use((context, next) =>
            {
                // Confirm what Kestrel detected without reading the body before the action.
                var canHaveBody = context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody;
                context.Response.Headers["X-Test-Body-Framing"] = $"{canHaveBody}:{context.Request.ContentLength}";
                return next(context);
            }), useKestrel: true, kestrelProtocols: HttpProtocols.Http2);
        var server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var transport = new SocketsHttpHandler { Expect100ContinueTimeout = TimeSpan.FromSeconds(30) };
        using var client = new HttpClient(transport) { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };

        foreach (var emptyDataFrame in new[] { false, true })
        {
            using var anonymous = await PostHttp2Async(client, route, emptyDataFrame ? "" : null);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            using var authorized = await PostHttp2Async(client, route, emptyDataFrame ? "" : null, actor.Id);
            Assert.Equal(HttpStatusCode.NotFound, authorized.StatusCode);
            var refusal = await authorized.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            Assert.Equal("invitation_unavailable", refusal?["code"]);
        }

        using var invalid = await PostHttp2Async(client, route, "x", actor.Id);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    /// <summary>A malformed Group path is rejected by routing before any body binding.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{")]
    [InlineData(null)]
    public async Task SharingRejectsInvalidGroupPathBeforeBinding(string? body)
    {
        using var db = CreateDbContext();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        using var content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/mobile/groups/invalid/peer-visibility", content);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>The released scalar contract checks policy before membership even when the JSON object is empty.</summary>
    [Fact]
    public async Task ScalarSharingChecksPolicyBeforeMembership()
    {
        using var db = CreateDbContext();
        var actor = AddBearer(db);
        var owner = TestDataFixtures.CreateUser();
        db.Users.Add(owner);
        var family = AddGroup(db, owner, "Family");
        var friends = AddGroup(db, owner, "Friends");
        await db.SaveChangesAsync();

        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", actor.Id);
        using var policy = await client.PostAsJsonAsync($"/api/mobile/groups/{family.Id}/peer-visibility", new { });
        Assert.Equal(HttpStatusCode.BadRequest, policy.StatusCode);
        var refusal = await policy.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("Personal sharing is unavailable for this group policy", refusal?["message"]);

        using var membership = await client.PostAsJsonAsync($"/api/mobile/groups/{friends.Id}/peer-visibility", new { });
        Assert.Equal(HttpStatusCode.Forbidden, membership.StatusCode);
    }

    /// <summary>Seeds the real token resolver's active account and credential.</summary>
    private static ApplicationUser AddBearer(ApplicationDbContext db)
    {
        var actor = TestDataFixtures.CreateUser();
        db.Users.Add(actor);
        db.ApiTokens.Add(new ApiToken
        {
            Name = "group-test", TokenHash = ApiTokenService.HashToken(actor.Id), UserId = actor.Id, User = actor
        });
        return actor;
    }

    /// <summary>Seeds a Group without changing the caller's membership.</summary>
    private static Group AddGroup(ApplicationDbContext db, ApplicationUser owner, string type)
    {
        var group = new Group { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = type, GroupType = type };
        db.Groups.Add(group);
        return group;
    }

    /// <summary>Null ends the stream in HEADERS; nonseekable content leaves length unknown and ends it with DATA.</summary>
    private static async Task<HttpResponseMessage> PostHttp2Async(HttpClient client, string route, string? body,
        string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
        if (body is not null)
        {
            var pipe = new Pipe();
            if (body.Length > 0) await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(body));
            await pipe.Writer.CompleteAsync();
            request.Content = new StreamContent(pipe.Reader.AsStream());

            // Force a HEADERS flush before HttpClient can coalesce the empty DATA into END_STREAM.
            request.Headers.ExpectContinue = body.Length == 0;
        }
        var response = await client.SendAsync(request);
        Assert.Equal(HttpVersion.Version20, response.Version);
        Assert.Equal(body is null ? "False:0" : "True:", Assert.Single(response.Headers.GetValues("X-Test-Body-Framing")));
        return response;
    }
}
