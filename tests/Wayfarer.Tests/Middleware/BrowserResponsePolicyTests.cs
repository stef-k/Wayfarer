using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Wayfarer.Middleware;
using Xunit;

namespace Wayfarer.Tests.Middleware;

/// <summary>Checks final response headers through real response-start and error-handler execution.</summary>
public sealed class BrowserResponsePolicyTests
{
    /// <summary>Only successful HTML explicitly approved by its controller may be embedded externally.</summary>
    [Theory]
    [InlineData(false, 200, "text/html", false)]
    [InlineData(true, 200, "text/html; charset=utf-8", true)]
    [InlineData(true, 404, "text/html", false)]
    [InlineData(true, 500, "text/html", false)]
    [InlineData(true, 302, "text/html", false)]
    [InlineData(true, 200, "application/json", false)]
    public async Task FinalResponse(bool marked, int status, string contentType, bool embed)
    {
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().Configure(app =>
        {
            app.UseMiddleware<BrowserResponsePolicyMiddleware>();
            app.Run(async context =>
            {
                if (marked) BrowserResponsePolicyMiddleware.AllowPublicEmbed(context);
                context.Response.StatusCode = status;
                context.Response.ContentType = contentType;
                // Existing downstream values must be replaced, never appended into conflicting policies.
                context.Response.Headers.Append("X-Frame-Options", new[] { "DENY", "ALLOWALL" });
                context.Response.Headers.Append("Content-Security-Policy", "frame-ancestors 'none'");
                await context.Response.WriteAsync("response");
            });
        })).StartAsync();
        using var response = await host.GetTestClient().GetAsync("/");
        AssertHeaders(response, embed);
    }

    /// <summary>A render exception clears headers and re-executes but never preserves the embed permission.</summary>
    [Fact]
    public async Task ExceptionReexecution_RemainsRestrictive()
    {
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().Configure(app =>
        {
            app.UseMiddleware<BrowserResponsePolicyMiddleware>();
            app.UseExceptionHandler("/error");
            app.Run(async context =>
            {
                if (context.Request.Path != "/error")
                {
                    BrowserResponsePolicyMiddleware.AllowPublicEmbed(context);
                    throw new InvalidOperationException("render failure");
                }
                context.Response.ContentType = "text/html";
                await context.Response.WriteAsync("error");
            });
        })).StartAsync();
        using var response = await host.GetTestClient().GetAsync("/");
        Assert.Equal(500, (int)response.StatusCode);
        AssertHeaders(response, false);
    }

    /// <summary>Exact singleton values also reject duplicate proxy/framework policies.</summary>
    internal static void AssertHeaders(HttpResponseMessage response, bool embed)
    {
        Assert.Equal(embed ? "frame-ancestors *" : "frame-ancestors 'self'",
            Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        if (embed) Assert.False(response.Headers.Contains("X-Frame-Options"));
        else Assert.Equal("SAMEORIGIN", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("strict-origin-when-cross-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }
}
