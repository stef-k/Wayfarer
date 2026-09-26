using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Wayfarer.Models;
using Wayfarer.Services;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>Real production Identity/MVC parts without database migrations, jobs or startup seeding.</summary>
internal static class IdentityRouteHost
{
    public static async Task<WebApplication> StartAsync(ApplicationDbContext db, string root)
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
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
            TrustedProxyConfiguration.Apply(options, ["192.0.2.10"], []));
        var app = builder.Build();
        // Test transport supplies the socket peer; only production forwarding interprets XFF.
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(
                context.Request.Headers["X-Test-Peer"].FirstOrDefault() ?? "192.0.2.20");
            await next(context);
        });
        app.UseForwardedHeaders();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/test-antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
            antiforgery.GetAndStoreTokens(context).RequestToken!);
        app.MapRazorPages();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    /// <summary>Obtain authentic antiforgery tokens with the matching cookie for a real form POST.</summary>
    public static async Task<string> AntiforgeryAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/test-antiforgery");
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        return await response.Content.ReadAsStringAsync();
    }
}
