using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Executes MVC result handling, including the lookup overload filter and inactive 401 envelope.</summary>
[Collection("API token singleton admission")]
public class ApiTokenHttpBoundaryTests : TestBase
{
    /// <summary>No authentication scheme is invoked for inactive bearer users.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InactiveIsExplicit401(bool mobile)
    {
        using var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(isActive: false);
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { User = user, UserId = user.Id, Name = "phone", Token = "inactive" });
        await db.SaveChangesAsync();
        using var server = Server(db);
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "inactive");
        var response = await client.GetAsync(mobile ? "/token-boundary/mobile" : "/token-boundary/legacy");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Invalid or missing API token.", await response.Content.ReadAsStringAsync());
    }

    /// <summary>Both caught and uncaught admission failures retain overload status and retry hint.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OverloadIsNeverInvalidAuthentication(bool caught, bool global)
    {
        using var db = CreateDbContext();
        var permits = new List<IDisposable>();
        try
        {
            for (var i = 0; i < (global ? 32 : 16); i++)
                permits.Add(ApiWorkAdmission.TokenLookups.TryAcquire(global ? $"ip-{i}" : "unknown", out _)!);
            using var server = Server(db);
            using var client = server.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", "anything");
            var response = await client.GetAsync(caught ? "/token-boundary/caught" : "/token-boundary/legacy");
            Assert.Equal(global ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter?.Delta);
        }
        finally { foreach (var permit in permits) permit.Dispose(); }
    }

    /// <summary>Uses normal MVC action/result execution without application startup or external providers.</summary>
    private static IHost Server(ApplicationDbContext db) => new HostBuilder().ConfigureWebHost(builder => builder
        .UseTestServer()
        .ConfigureServices(services =>
        {
            services.AddSingleton(db);
            services.AddHttpContextAccessor();
            services.AddScoped<IMobileCurrentUserAccessor, MobileCurrentUserAccessor>();
            services.AddControllers(options => options.Filters.Add<ApiTokenAdmissionFilter>())
                .AddApplicationPart(typeof(TokenBoundaryController).Assembly);
        })
        .Configure(app => { app.UseRouting(); app.UseEndpoints(endpoints => endpoints.MapControllers()); })).Start();
}

/// <summary>Thin test adapter executes the production legacy authority through MVC.</summary>
[Route("token-boundary")]
public sealed class TokenBoundaryController(ApplicationDbContext db)
    : BaseApiController(db, NullLogger<BaseApiController>.Instance)
{
    [HttpGet("legacy")]
    public IActionResult Legacy() => GetUserFromToken() == null
        ? Unauthorized("Invalid or missing API token.") : Ok();

    [HttpGet("caught")]
    public IActionResult Caught()
    {
        try { return Legacy(); }
        catch (Exception) { return StatusCode(500); }
    }
}

/// <summary>Thin test adapter executes the production mobile guard through MVC.</summary>
[Route("token-boundary/mobile")]
public sealed class MobileTokenBoundaryController(ApplicationDbContext db, IMobileCurrentUserAccessor accessor)
    : MobileApiController(db, NullLogger<BaseApiController>.Instance, accessor)
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var (_, error) = await EnsureAuthenticatedUserAsync();
        return error ?? Ok();
    }
}

/// <summary>Only saturation tests reserve the process singleton; exclude unrelated parallel requests.</summary>
[CollectionDefinition("API token singleton admission", DisableParallelization = true)]
public sealed class ApiTokenAdmissionCollection;

/// <summary>Trip-specific invalid-token envelope through the shared legacy authority.</summary>
public partial class ApiTripsControllerTests
{
    /// <summary>Inactive bearer accounts cannot access their trip list through the legacy adapter.</summary>
    [Fact]
    public void GetUserTrips_RejectsInactiveBearer()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(isActive: false);
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { Token = "inactive", User = user, UserId = user.Id, Name = "phone" });
        db.SaveChanges();
        var controller = BuildController(db);
        controller.Request.Headers.Authorization = "Bearer inactive";
        var result = Assert.IsType<UnauthorizedObjectResult>(controller.GetUserTrips());
        Assert.Equal("Missing or invalid API token.", result.Value);
    }

}
