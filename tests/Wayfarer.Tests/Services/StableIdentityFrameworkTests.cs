using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Qualifies actual framework payloads and explicit operation purposes over a persistent complete ring.</summary>
public sealed class StableIdentityFrameworkTests
{
    /// <summary>Legacy cookies, forms, Identity links and operation tokens invalidate; stable reissues cross roots.</summary>
    [Fact]
    public async Task Cutover_InvalidatesLegacyFrameworkPayloadsAndAcceptsStableReissuesAcrossRoots()
    {
        using var directory = new TestDirectory();
        var sourceRoot = Directory.CreateDirectory(Path.Combine(directory.Path, "source")).FullName;
        var targetRoot = Directory.CreateDirectory(Path.Combine(directory.Path, "target")).FullName;
        var ring = Path.Combine(directory.Path, "ring");
        await using var legacy = Host(sourceRoot, ring, stable: false);
        var oldCookie = await IssueCookieAsync(legacy);
        var oldForm = IssueForm(legacy);
        var oldIdentity = await IdentityTokenAsync(legacy);
        var oldOperations = Operations(legacy.Services.GetRequiredService<IDataProtectionProvider>());
        var oldTokens = oldOperations.Select(operation => operation.Issue()).ToArray();
        await using var stable = Host(sourceRoot, ring, stable: true);
        Assert.False(await AuthenticateAsync(stable, oldCookie));
        Assert.False(await ValidateFormAsync(stable, oldForm));
        Assert.False(await ValidateIdentityAsync(stable, oldIdentity));
        var stableOperations = Operations(stable.Services.GetRequiredService<IDataProtectionProvider>());
        var stableTokens = stableOperations.Select(operation => operation.Issue()).ToArray();
        for (var index = 0; index < stableOperations.Length; index++)
        {
            Assert.False(stableOperations[index].Read(oldTokens[index]));
            Assert.True(stableOperations[index].Read(stableTokens[index]));
        }
        var cookie = await IssueCookieAsync(stable);
        var form = IssueForm(stable);
        var identity = await IdentityTokenAsync(stable);
        Assert.True(await AuthenticateAsync(stable, cookie));
        Assert.True(await ValidateFormAsync(stable, form));
        Assert.True(await ValidateIdentityAsync(stable, identity));

        var targetRing = Directory.CreateDirectory(Path.Combine(directory.Path, "copied-ring")).FullName;
        foreach (var file in Directory.GetFiles(ring)) File.Copy(file, Path.Combine(targetRing, Path.GetFileName(file)));
        await using var target = Host(targetRoot, targetRing, stable: true);
        Assert.Equal("Wayfarer", target.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);
        Assert.True(await AuthenticateAsync(target, cookie));
        Assert.True(await ValidateFormAsync(target, form));
        Assert.True(await ValidateIdentityAsync(target, identity));
        var targetOperations = Operations(target.Services.GetRequiredService<IDataProtectionProvider>());
        for (var index = 0; index < targetOperations.Length; index++)
            Assert.True(targetOperations[index].Read(stableTokens[index]));
    }

    /// <summary>Exercises the four production issue/read paths with a fixed fixture scope and normal expiry.</summary>
    private static (Func<string> Issue, Func<string, bool> Read)[] Operations(IDataProtectionProvider provider)
    {
        var trip = Guid.Parse("a0000000-0000-0000-0000-000000000001");
        var segment = Guid.Parse("a0000000-0000-0000-0000-000000000002");
        var aggregate = new SegmentAggregateTokenService(provider);
        var clear = new SegmentRouteClearConfirmation(provider, TimeProvider.System);
        var lifecycle = new LifecycleDependencyConfirmation(provider);
        var dependencies = new LifecycleDependencies([segment], [], [], [], []);
        var proposal = new ExternalRouteProposalContextService(provider);
        var binding = new ExternalRouteProposalBinding(Guid.Empty, trip, segment, "fixture-user", "geometry", "anchors", null, "version");
        return
        [
            (() => aggregate.Issue("fixture-user", trip, segment, 1),
                token => aggregate.TryRead(token, "fixture-user", trip, segment, out var version) && version == 1),
            (() => clear.Issue(segment, "fingerprint").Token, token => clear.IsValid(token, segment, "fingerprint")),
            (() => lifecycle.Create("conflict", "delete", "fixture-user", trip, segment, dependencies).ConfirmationToken,
                token => lifecycle.IsValid(token, "delete", "fixture-user", trip, segment, dependencies)),
            (() => proposal.Issue(binding).Token, token => proposal.TryRead(token, out var read) && read == binding)
        ];
    }

    /// <summary>Uses the normal stable registration or framework hosted legacy registration without a discriminator override.</summary>
    private static WebApplication Host(string root, string ring, bool stable)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.Logging.ClearProviders();
        if (stable)
        {
            builder.Configuration["DataProtection:KeyRingPath"] = ring;
            builder.AddWayfarerDataProtection();
        }
        else builder.Services.AddDataProtection().PersistKeysToFileSystem(Directory.CreateDirectory(ring));
        builder.Services.AddAuthentication("test-cookie").AddCookie("test-cookie", options => options.Cookie.Name = "test-cookie");
        builder.Services.AddAntiforgery(options => { options.Cookie.Name = "test-form"; options.HeaderName = "X-CSRF"; });
        return builder.Build();
    }

    /// <summary>Creates an isolated request with the host's actual framework services.</summary>
    private static DefaultHttpContext Context(IServiceProvider services) => new() { RequestServices = services };

    /// <summary>Issues a real authentication middleware cookie, without exposing its protected payload.</summary>
    private static async Task<string> IssueCookieAsync(WebApplication host)
    {
        using var scope = host.Services.CreateScope();
        var context = Context(scope.ServiceProvider);
        await context.SignInAsync("test-cookie", new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "fixture-user")], "test-cookie")));
        return context.Response.Headers.SetCookie.Single()!.Split(';')[0];
    }

    /// <summary>Authenticates the protected cookie through the framework handler.</summary>
    private static async Task<bool> AuthenticateAsync(WebApplication host, string cookie)
    {
        using var scope = host.Services.CreateScope();
        var context = Context(scope.ServiceProvider);
        context.Request.Headers.Cookie = cookie;
        return (await context.AuthenticateAsync("test-cookie")).Succeeded;
    }

    /// <summary>Issues the real antiforgery cookie/request pair.</summary>
    private static (string Cookie, string Token) IssueForm(WebApplication host)
    {
        using var scope = host.Services.CreateScope();
        var context = Context(scope.ServiceProvider);
        var tokens = scope.ServiceProvider.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context);
        return (context.Response.Headers.SetCookie.Single()!.Split(';')[0], tokens.RequestToken!);
    }

    /// <summary>Validates a non-safe request using the framework antiforgery implementation.</summary>
    private static async Task<bool> ValidateFormAsync(WebApplication host, (string Cookie, string Token) form)
    {
        using var scope = host.Services.CreateScope();
        var context = Context(scope.ServiceProvider);
        context.Request.Method = "POST";
        context.Request.Headers.Cookie = form.Cookie;
        context.Request.Headers["X-CSRF"] = form.Token;
        return await scope.ServiceProvider.GetRequiredService<IAntiforgery>().IsRequestValidAsync(context);
    }

    /// <summary>Uses the actual Identity Data Protection token provider with a fixed fixture user store.</summary>
    private static (DataProtectorTokenProvider<ApplicationUser> Provider, UserManager<ApplicationUser> Manager, ApplicationUser User)
        Identity(WebApplication host)
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        store.Setup(item => item.GetUserIdAsync(It.IsAny<ApplicationUser>(), It.IsAny<CancellationToken>())).ReturnsAsync("fixture-user");
        var manager = new UserManager<ApplicationUser>(store.Object, Options.Create(new IdentityOptions()),
            new PasswordHasher<ApplicationUser>(), [], [], new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), host.Services, host.Services.GetRequiredService<ILogger<UserManager<ApplicationUser>>>());
        return (new(host.Services.GetRequiredService<IDataProtectionProvider>(), Options.Create(new DataProtectionTokenProviderOptions()),
            host.Services.GetRequiredService<ILogger<DataProtectorTokenProvider<ApplicationUser>>>()), manager, new() { Id = "fixture-user", DisplayName = "Fixture" });
    }

    /// <summary>Issues a representative password reset token.</summary>
    private static async Task<string> IdentityTokenAsync(WebApplication host)
    {
        var identity = Identity(host);
        using var manager = identity.Manager;
        return await identity.Provider.GenerateAsync("ResetPassword", manager, identity.User);
    }

    /// <summary>Validates the link against the current application identity.</summary>
    private static async Task<bool> ValidateIdentityAsync(WebApplication host, string token)
    {
        var identity = Identity(host);
        using var manager = identity.Manager;
        return await identity.Provider.ValidateAsync("ResetPassword", token, manager, identity.User);
    }
}
