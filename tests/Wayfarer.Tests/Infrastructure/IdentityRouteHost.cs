using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using Wayfarer.Models;
using Wayfarer.Models.Options;
using Wayfarer.Services;
using Wayfarer.Services.LocationImports;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>Real production Identity/MVC parts without database migrations, jobs or startup seeding.</summary>
internal static class IdentityRouteHost
{
    /// <summary>Protects a representative Identity ticket with the persisted account's security stamp.</summary>
    public static string Cookie(IServiceProvider services, string userId, string role = "User")
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stamp = db.Users.Where(user => user.Id == userId).Select(user => user.SecurityStamp).SingleOrDefault();
        var scheme = IdentityConstants.ApplicationScheme;
        var options = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(scheme);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, userId),
            new(ClaimTypes.Role, role)
        };
        if (stamp != null) claims.Add(new("AspNet.Identity.SecurityStamp", stamp));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
        var ticket = new AuthenticationTicket(principal,
            new AuthenticationProperties
            { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10) }, scheme);
        return options.Cookie.Name + "=" + options.TicketDataFormat.Protect(ticket);
    }

    /// <summary>Starts production MVC/Identity parts with optional service and pre-routing middleware setup.</summary>
    public static async Task<WebApplication> StartAsync(ApplicationDbContext db, string root,
        Action<IServiceCollection>? configure = null, Action<WebApplication>? configureApp = null)
    {
        var assembly = typeof(ApplicationUser).Assembly;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = assembly.GetName().Name,
            EnvironmentName = "Development"
        });
        // The host intentionally omits startup-only job/database services.
        builder.Host.UseDefaultServiceProvider(options => options.ValidateOnBuild = false);
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Database=unused;Username=unused",
            ["DataProtection:KeyRingPath"] = Path.Combine(root, "keys"),
            ["Storage:DataRoot"] = Path.Combine(root, "data"),
            ["Storage:CacheRoot"] = Path.Combine(root, "cache"),
            ["Storage:LogRoot"] = Path.Combine(root, "logs"),
            ["Storage:TempRoot"] = Path.Combine(root, "temp")
        });
        // Invoke actual registrations so route ownership and the filter installation cannot drift.
        foreach (var name in new[] { "ConfigureIdentity", "ConfigureServices" })
            assembly.GetType("Program")!.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Single(method => method.Name.Contains("g__" + name + "|")).Invoke(null, [builder]);
        builder.Services.AddSingleton(db);
        builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
        builder.Services.AddSingleton<StoragePaths>();
        builder.Services.AddSingleton<LocationImportStagedFiles>();
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
            TrustedProxyConfiguration.Apply(options, ["192.0.2.10"], []));
        // Routed mutation tests replace only external work at existing service seams.
        configure?.Invoke(builder.Services);
        var app = builder.Build();
        configureApp?.Invoke(app);
        // Test transport supplies the socket peer; only production forwarding interprets XFF.
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(
                context.Request.Headers["X-Test-Peer"].FirstOrDefault() ?? "192.0.2.20");
            await next(context);
        });
        app.UseMiddleware<Wayfarer.Middleware.BrowserResponsePolicyMiddleware>();
        app.UseForwardedHeaders();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/test-antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
            antiforgery.GetAndStoreTokens(context).RequestToken!);
        app.MapRazorPages();
        app.MapControllers();
        assembly.GetType("Program")!.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method => method.Name.Contains("g__ConfigureAreas|")).Invoke(null, [app]);
        await app.StartAsync();
        return app;
    }

    /// <summary>Obtain authentic antiforgery tokens with the matching cookie for a real form POST.</summary>
    public static async Task<string> AntiforgeryAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/test-antiforgery");
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal)).Split(';')[0]);
        return await response.Content.ReadAsStringAsync();
    }
}
