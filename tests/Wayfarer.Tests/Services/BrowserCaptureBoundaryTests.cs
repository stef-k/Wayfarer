using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Behavioral proof of the shared capture origin, admission and real Chromium egress boundary.</summary>
[Collection(PlaywrightEnvironmentTestCollection.Name)]
public sealed class BrowserCaptureBoundaryTests
{
    /// <summary>The third workflow is rejected synchronously and double release cannot create extra capacity.</summary>
    [Fact]
    public void AdmissionIsImmediateAndReleaseIsExactOnce()
    {
        var admission = new BrowserAdmission();
        var first = admission.TryAcquire();
        using var second = admission.TryAcquire();
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(admission.TryAcquire());
        first.Dispose();
        first.Dispose();
        using var replacement = admission.TryAcquire();
        Assert.NotNull(replacement);
        Assert.Null(admission.TryAcquire());
    }

    /// <summary>Only a configured public authority and local HTTP listener permit browser startup.</summary>
    [Theory]
    [InlineData("*", "http://*:5000", false)]
    [InlineData("localhost", "http://*:5000", false)]
    [InlineData("127.0.0.1", "http://*:5000", false)]
    [InlineData("wayfarer.example.org", "https://*:5001", false)]
    [InlineData("wayfarer.example.org", "http://*:5000", true)]
    public void CaptureAuthorityIsServerOwned(string host, string listener, bool allowed)
    {
        var policy = BrowserCapturePolicy.Resolve(Configuration(host, listener));
        Assert.Equal(allowed, policy != null);
    }

    /// <summary>Public off-origin, private, internal, alternate-port and credential-bearing URLs all fail closed.</summary>
    [Theory]
    [InlineData("https://example.org/image")]
    [InlineData("http://127.0.0.1:5500/")]
    [InlineData("http://169.254.169.254/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://internal/")]
    [InlineData("http://wayfarer.example.org:5501/")]
    [InlineData("http://user@wayfarer.example.org:5500/")]
    public void RejectsEveryOtherNetworkAuthority(string target)
    {
        var policy = BrowserCapturePolicy.Resolve(Configuration("wayfarer.example.org", "http://*:5500"))!;
        Assert.True(policy.Allows(policy.Url("/Public/Trips")));
        Assert.False(policy.Allows(target));
    }

    /// <summary>A real local server records any escaped contact; redirects, subresources and sockets must not reach it.</summary>
    [Fact, Trait("Category", "RequiresPlaywright")]
    public async Task ChromiumAllowsFirstPartyButNeverContactsRedirectOrOffOriginTargets()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        var deniedContacts = 0;
        var firstPartyContacts = 0;
        string? capturedCookie = null;
        server.MapGet("/{**path}", async context =>
        {
            if (context.Request.Host.Host != "wayfarer.example.org") Interlocked.Increment(ref deniedContacts);
            else Interlocked.Increment(ref firstPartyContacts);
            if (context.Request.Path == "/redirect")
            {
                context.Response.Redirect($"http://127.0.0.1:{context.Request.Host.Port}/forbidden");
                return;
            }
            if (context.Request.Path == "/private") capturedCookie = context.Request.Headers.Cookie;
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<html><body>capture</body></html>");
        });
        await server.StartAsync();
        var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var port = new Uri(address).Port;
        var policy = BrowserCapturePolicy.Resolve(Configuration("wayfarer.example.org", address))!;
        await using var workflow = await BrowserWorkflow.StartAsync(policy, CancellationToken.None);
        var services = new ServiceCollection();
        services.AddAuthentication().AddCookie(IdentityConstants.ApplicationScheme);
        using var provider = services.BuildServiceProvider();
        var caller = new DefaultHttpContext { RequestServices = provider };
        caller.Request.Headers.Cookie = ".AspNetCore.Identity.Application=owner-ticket; unrelated=secret";
        var cookies = policy.ApplicationCookies(caller);
        Assert.Single(cookies);
        await using var context = await workflow.NewContextAsync(800, 450, cookies);
        var page = await context.Context.NewPageAsync();
        Assert.True((await page.GotoAsync(policy.Url("/")))!.Ok);
        Assert.True(await page.EvaluateAsync<bool>("async () => (await fetch('/private')).ok"));
        Assert.Equal(".AspNetCore.Identity.Application=owner-ticket", capturedCookie);
        Assert.False(await page.EvaluateAsync<bool>("async url => { try { await fetch(url); return true; } catch { return false; } }",
            $"http://127.0.0.1:{port}/forbidden"));
        Assert.False(await page.EvaluateAsync<bool>("async () => { try { await fetch('/redirect'); return true; } catch { return false; } }"));
        Assert.False(await page.EvaluateAsync<bool>("async () => { try { await navigator.serviceWorker.register('/worker.js'); return true; } catch { return false; } }"));
        Assert.Equal("closed", await page.EvaluateAsync<string>("url => new Promise(resolve => { let ws; try { ws = new WebSocket(url); } catch { resolve('closed'); return; } ws.onopen = () => resolve('open'); ws.onclose = () => resolve('closed'); ws.onerror = () => resolve('closed'); })",
            $"ws://127.0.0.1:{port}/socket"));
        await Assert.ThrowsAsync<PlaywrightException>(() => page.GotoAsync(policy.Url("/redirect")));
        Assert.True(firstPartyContacts >= 3);
        Assert.Equal(0, deniedContacts);
    }

    /// <summary>PDF viewports are closed independently of public thumbnail dimensions.</summary>
    [Theory]
    [InlineData(800, 800, true)]
    [InlineData(600, 600, true)]
    [InlineData(800, 450, false)]
    [InlineData(10000, 10000, false)]
    public void PdfMapVariantsAreFixed(int width, int height, bool valid)
    {
        if (valid) BrowserCapturePolicy.ValidateMap(width, height);
        else Assert.Throws<ArgumentOutOfRangeException>(() => BrowserCapturePolicy.ValidateMap(width, height));
    }

    /// <summary>Builds only explicit test listener/authority configuration.</summary>
    internal static IConfiguration Configuration(string host, string listener) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = host, ["Kestrel:Endpoints:Http:Url"] = listener
        }).Build();
}
