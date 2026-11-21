using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.RazorPages.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wayfarer.Areas.Identity.Pages.Account;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Util;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Real compiled page ownership and HTTP admission, antiforgery and flow contracts.</summary>
public sealed class IdentityAttemptRouteTests : TestBase
{
    /// <summary>Production mutations require the shared policy; Development retains its legacy seed policy.</summary>
    [Theory]
    [InlineData("Production", 15)]
    [InlineData("Development", 6)]
    public void WebIdentity_ConfiguresPasswordPolicyForEnvironment(string environment, int minimumLength)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        typeof(ApplicationUser).Assembly.GetType("Program")!.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method => method.Name.Contains("g__ConfigureIdentity|")).Invoke(null, [builder]);
        using var services = builder.Services.BuildServiceProvider();
        var password = services.GetRequiredService<IOptions<IdentityOptions>>().Value.Password;
        Assert.Equal(minimumLength, password.RequiredLength);
        Assert.True(password.RequireUppercase);
        Assert.True(password.RequireLowercase);
        Assert.True(password.RequireDigit);
        Assert.True(password.RequireNonAlphanumeric);
    }

    /// <summary>Pins the complete compiled route inventory, model ownership, and filters.</summary>
    [Fact]
    public async Task CompiledPages_PreserveOwnershipAuthorizationAndLockout()
    {
        await using var app = await IdentityRouteHost.StartAsync(CreateDbContext(), CreateTestDirectory(), IdentityRouteHost.WithStartupSettings);
        var loader = app.Services.GetRequiredService<PageLoader>();
        var pages = new Dictionary<string, CompiledPageActionDescriptor>();
        foreach (var endpoint in ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints))
        {
            if (endpoint.Metadata.GetMetadata<PageActionDescriptor>() is not { AreaName: "Identity" } page) continue;
            pages.TryAdd(page.ViewEnginePath, await loader.LoadAsync(page, endpoint.Metadata));
        }
        // Every compiled endpoint has an accountable model, including the handler-only download.
        string[] account =
        [
            "AccessDenied", "ConfirmEmail", "ConfirmEmailChange", "ExternalLogin", "ForgotPassword",
            "ForgotPasswordConfirmation", "Lockout", "Login", "LoginWith2fa", "LoginWithRecoveryCode",
            "Logout", "Register", "RegisterConfirmation", "ResendEmailConfirmation", "ResetPassword",
            "ResetPasswordConfirmation"
        ];
        string[] manage =
        [
            "ChangePassword", "DeletePersonalData", "Disable2fa", "DownloadPersonalData", "Email",
            "EnableAuthenticator", "ExternalLogins", "GenerateRecoveryCodes", "Index", "PersonalData",
            "ResetAuthenticator", "SetPassword", "ShowRecoveryCodes", "TwoFactorAuthentication"
        ];
        var expected = account.Select(name => "/Account/" + name)
            .Concat(manage.Select(name => "/Account/Manage/" + name)).Append("/Error");
        Assert.Equal(expected.Order(), pages.Keys.Order());
        var local = new Dictionary<string, Type>
        {
            ["/Account/Login"] = typeof(LoginModel),
            ["/Account/Register"] = typeof(RegisterModel),
            ["/Account/Manage/Index"] = typeof(Wayfarer.Areas.Identity.Pages.Account.Manage.IndexModel),
            ["/Account/Manage/EnableAuthenticator"] = typeof(Wayfarer.Areas.Identity.Pages.Account.Manage.EnableAuthenticatorModel)
        };
        foreach (var (path, descriptor) in pages)
        {
            var model = descriptor.ModelTypeInfo!.AsType();
            if (local.TryGetValue(path, out var applicationModel)) Assert.Equal(applicationModel, model);
            else Assert.Equal("Microsoft.AspNetCore.Identity.UI", model.Assembly.GetName().Name);
        }
        Assert.All(pages.Values, page => Assert.Contains(page.FilterDescriptors,
            filter => filter.Filter.GetType().Name.Contains("AutoValidateAntiforgery")));
        Assert.All(pages.Values, page => Assert.DoesNotContain(page.FilterDescriptors,
            filter => filter.Filter is Microsoft.AspNetCore.Mvc.IgnoreAntiforgeryTokenAttribute));
        Assert.All(pages.Where(page => page.Key.StartsWith("/Account/Manage/")),
            page => Assert.Contains(page.Value.EndpointMetadata, metadata => metadata is IAuthorizeData));
        var options = app.Services.GetRequiredService<IOptions<IdentityOptions>>().Value;
        Assert.Equal(5, options.Lockout.MaxFailedAccessAttempts);
        Assert.Equal(TimeSpan.FromMinutes(15), options.Lockout.DefaultLockoutTimeSpan);
        Assert.True(options.Lockout.AllowedForNewUsers);
    }

    /// <summary>Checks localized 429 responses after antiforgery across the admitted route families.</summary>
    [Fact]
    public async Task ExhaustedClient_IsRejectedAcrossPasswordRecoveryAndTokenPages()
    {
        var db = CreateDbContext();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), IdentityRouteHost.WithStartupSettings);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el-CY, en-US;q=0.5");
        var token = await IdentityRouteHost.AntiforgeryAsync(client);
        var admission = app.Services.GetRequiredService<IdentityAttemptAdmission>();
        for (var i = 0; i < IdentityAttemptAdmission.AttemptLimit; i++)
            Assert.Equal(0, admission.Admit(IPAddress.Parse("192.0.2.20")));
        foreach (var page in new[] { "Login", "Register", "LoginWith2fa", "LoginWithRecoveryCode",
                     "ForgotPassword", "ResetPassword", "ResendEmailConfirmation" })
        {
            // Unknown handler names fall back to the unnamed handler in Razor Pages.
            using var response = await client.PostAsync($"/Identity/Account/{page}?handler=Unknown", Form(token));
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.InRange(response.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 300);
            Assert.Equal("el-GR", Assert.Single(response.Content.Headers.ContentLanguage));
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal("Έγιναν υπερβολικά πολλές προσπάθειες. Δοκιμάστε ξανά αργότερα.",
                await response.Content.ReadAsStringAsync());
        }
        foreach (var path in new[] { "ConfirmEmail?userId=missing&code=invalid", "ConfirmEmailChange?userId=missing&email=a&code=invalid",
                     "RegisterConfirmation?email=a", "ConfirmEmail?handler=Unknown&userId=missing&code=invalid" })
        {
            using var response = await client.GetAsync("/Identity/Account/" + path);
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        }
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head,
            "/Identity/Account/ConfirmEmail?userId=missing&code=invalid"));
        Assert.Equal(HttpStatusCode.TooManyRequests, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsStringAsync());
        Assert.Equal("el-GR", Assert.Single(head.Content.Headers.ContentLanguage));
        using var invalidForm = await client.PostAsync("/Identity/Account/Login", Form("invalid"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidForm.StatusCode);
        // Missing antiforgery is rejected before the exhausted admission budget or Login handler.
        using var missingToken = await client.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
        foreach (var path in new[] { "Login", "ForgotPasswordConfirmation", "ResetPasswordConfirmation", "ExternalLogin",
                     "ConfirmEmail", "ConfirmEmailChange", "RegisterConfirmation", "Manage" })
        {
            using var response = await client.GetAsync("/Identity/Account/" + path);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }
        client.DefaultRequestHeaders.AcceptLanguage.Clear();
        using var english = await client.PostAsync("/Identity/Account/Login", Form(token));
        Assert.Equal(HttpStatusCode.TooManyRequests, english.StatusCode);
        Assert.Equal("Too many account attempts. Please try again later.", await english.Content.ReadAsStringAsync());
        using var api = await client.GetAsync("/api/settings");
        Assert.True(api.StatusCode == HttpStatusCode.OK, await api.Content.ReadAsStringAsync());
    }

    /// <summary>Checks that browser admission leaves mobile bearer requests available.</summary>
    [Fact]
    public async Task ExhaustedClient_MobileBearerStillWorksWithoutBrowserCookies()
    {
        var db = CreateDbContext();
        var user = new ApplicationUser { UserName = "mobile-user", DisplayName = "User", IsActive = true };
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { UserId = user.Id, User = user, Name = "existing", TokenHash = ApiTokenService.HashToken("test-bearer") });
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), IdentityRouteHost.WithStartupSettings);
        var admission = app.Services.GetRequiredService<IdentityAttemptAdmission>();
        for (var i = 0; i < IdentityAttemptAdmission.AttemptLimit; i++) admission.Admit(IPAddress.Parse("192.0.2.20"));
        using var client = app.GetTestClient();
        using var missing = await client.GetAsync("/api/mobile/groups");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Null(missing.Headers.Location);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "test-bearer");
        using var valid = await client.GetAsync("/api/mobile/groups");
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal("[]", await valid.Content.ReadAsStringAsync());
    }

    /// <summary>Preserves registration policy for enabled, disabled, and absent database settings.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Registration_RoutedGetAndPost_PreservePolicyAndOpenSideEffects(bool? open)
    {
        var db = CreateDbContext();
        if (open.HasValue)
        {
            db.ApplicationSettings.Add(new ApplicationSettings { IsRegistrationOpen = open.Value });
            await db.SaveChangesAsync();
        }
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), IdentityRouteHost.WithStartupSettings);
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>().CreateAsync(new IdentityRole("User"));
        using var client = app.GetTestClient();
        using var get = await client.GetAsync("/Identity/Account/Register");
        Assert.Equal(open == true ? HttpStatusCode.OK : HttpStatusCode.Redirect, get.StatusCode);
        var token = await IdentityRouteHost.AntiforgeryAsync(client);
        using var post = await client.PostAsync("/Identity/Account/Register", Form(token,
            ("Input.Username", "new-user"), ("Input.DisplayName", "New User"),
            ("Input.Password", "Correct-Password1!"), ("Input.ConfirmPassword", "Correct-Password1!")));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        if (open == true)
        {
            Assert.StartsWith("/Identity/Account/RegisterConfirmation?", post.Headers.Location!.OriginalString);
            Assert.Contains("username=new-user", post.Headers.Location.OriginalString);
            Assert.Single(db.Users);
            Assert.Single(db.UserRoles);
            Assert.Equal("Wayfarer Incoming Location Data API Token", Assert.Single(db.ApiTokens).Name);
            Assert.DoesNotContain(post.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
                cookie => cookie.Contains(".AspNetCore.Identity.Application="));
        }
        else
        {
            Assert.Equal("/Home/RegistrationClosed", post.Headers.Location!.OriginalString);
            Assert.Empty(db.Users);
            Assert.Empty(db.UserRoles);
            Assert.Empty(db.ApiTokens);
        }
    }

    /// <summary>Checks that client admission trusts only the forwarded peer configured by the host.</summary>
    [Theory]
    [InlineData("192.0.2.10", false)]
    [InlineData("192.0.2.20", true)]
    public async Task Forwarding_OnlyTrustedPeerCanSelectAnotherClient(string peer, bool rejected)
    {
        var db = CreateDbContext();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), IdentityRouteHost.WithStartupSettings);
        var admission = app.Services.GetRequiredService<IdentityAttemptAdmission>();
        for (var i = 0; i < IdentityAttemptAdmission.AttemptLimit; i++) admission.Admit(IPAddress.Parse(peer));
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Peer", peer);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.1");
        var token = await IdentityRouteHost.AntiforgeryAsync(client);
        using var response = await client.PostAsync("/Identity/Account/ForgotPassword", Form(token, ("Input.Email", "absent@example.test")));
        Assert.Equal(rejected ? HttpStatusCode.TooManyRequests : HttpStatusCode.Redirect, response.StatusCode);
        if (!rejected) Assert.Equal("/Identity/Account/ForgotPasswordConfirmation", response.Headers.Location!.OriginalString);
    }

    /// <summary>Submit ordinary browser fields with real antiforgery rather than disabling its filter.</summary>
    private static FormUrlEncodedContent Form(string token, params (string Key, string Value)[] fields) =>
        new(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value))
            .Append(new("__RequestVerificationToken", token)));
}
