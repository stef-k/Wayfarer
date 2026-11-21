using System.Net;
using System.Text;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Admitted requests still execute the real Identity sign-in, lockout and token handlers.</summary>
public sealed class IdentityAdmittedFlowTests : TestBase
{
    /// <summary>Checks the joint admission budget and the subsequent independent account lockout.</summary>
    [Fact]
    public async Task PasswordSpraying_UsesOneBudget_WhileAccountLockoutStillApplies()
    {
        var db = CreateDbContext();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), IdentityRouteHost.WithStartupSettings);
        using var scope = app.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = "lockout-user", DisplayName = "User", IsActive = true };
        Assert.True((await manager.CreateAsync(user, "Correct-Password1!")).Succeeded);
        using var client = app.GetTestClient();
        var token = await IdentityRouteHost.AntiforgeryAsync(client);
        for (var i = 0; i < 5; i++)
        {
            using var response = await client.PostAsync("/Identity/Account/Login",
                Form(token, ("Input.Username", user.UserName), ("Input.Password", "wrong")));
            Assert.True(response.StatusCode == (i == 4 ? HttpStatusCode.Redirect : HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
            if (i == 4) Assert.Equal("/Identity/Account/Lockout", response.Headers.Location!.OriginalString);
        }
        Assert.True(await manager.IsLockedOutAsync(user));
        for (var i = 5; i < IdentityAttemptAdmission.AttemptLimit; i++)
        {
            using var response = await client.PostAsync("/Identity/Account/Login",
                Form(token, ("Input.Username", $"spray-{i}"), ("Input.Password", "wrong")));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var denied = await client.PostAsync("/Identity/Account/Login",
            Form(token, ("Input.Username", "another-user"), ("Input.Password", "wrong")));
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Peer", "192.0.2.21");
        using var independent = await client.PostAsync("/Identity/Account/Login",
            Form(token, ("Input.Username", user.UserName), ("Input.Password", "Correct-Password1!")));
        Assert.Equal("/Identity/Account/Lockout", independent.Headers.Location!.OriginalString);
        Assert.True(await manager.IsLockedOutAsync(user));
        await manager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(-1));
        using var success = await client.PostAsync("/Identity/Account/Login?returnUrl=%2Fdestination",
            Form(token, ("Input.Username", user.UserName), ("Input.Password", "Correct-Password1!")));
        Assert.Equal("/destination", success.Headers.Location!.OriginalString);
        Assert.Contains(success.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(".AspNetCore.Identity.Application="));
    }

    /// <summary>Exercises framework 2FA rejection and recovery sign-in with their original cookies.</summary>
    [Fact]
    public async Task TwoFactorAndRecovery_KeepPendingCookieAndSuccessfulRedirect()
    {
        var db = CreateDbContext();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), IdentityRouteHost.WithStartupSettings);
        using var scope = app.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = "twofactor-user", DisplayName = "User", IsActive = true };
        Assert.True((await manager.CreateAsync(user, "Correct-Password1!")).Succeeded);
        await manager.ResetAuthenticatorKeyAsync(user);
        await manager.SetTwoFactorEnabledAsync(user, true);
        var recovery = (await manager.GenerateNewTwoFactorRecoveryCodesAsync(user, 1))!.Single();
        using var client = app.GetTestClient();
        var token = await IdentityRouteHost.AntiforgeryAsync(client);
        using var login = await client.PostAsync("/Identity/Account/Login?returnUrl=%2Fdestination",
            Form(token, ("Input.Username", user.UserName), ("Input.Password", "Correct-Password1!")));
        Assert.StartsWith("/Identity/Account/LoginWith2fa?", login.Headers.Location!.OriginalString);
        // Preserve the real pending-2FA cookie alongside the antiforgery cookie.
        var cookies = client.DefaultRequestHeaders.GetValues("Cookie").Single();
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", cookies + "; " + string.Join("; ",
            login.Headers.GetValues("Set-Cookie").Select(cookie => cookie.Split(';')[0])));
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el");
        using var invalid = await client.PostAsync("/Identity/Account/LoginWith2fa",
            Form(token, ("Input.TwoFactorCode", "xxxxxx")));
        var invalidHtml = await invalid.Content.ReadAsStringAsync();
        Assert.True(invalid.StatusCode == HttpStatusCode.OK, invalidHtml);
        var challenge = new HtmlParser().ParseDocument(invalidHtml);
        Assert.Equal("el-GR", challenge.DocumentElement.GetAttribute("lang"));
        Assert.Contains("Μη έγκυρος κωδικός εφαρμογής επαλήθευσης.", challenge.QuerySelector("main .validation-summary-errors")?.TextContent);
        Assert.Null(challenge.QuerySelector("#language-open"));
        using var success = await client.PostAsync("/Identity/Account/LoginWithRecoveryCode?returnUrl=%2Fdestination",
            Form(token, ("Input.RecoveryCode", recovery)));
        Assert.Equal("/destination", success.Headers.Location!.OriginalString);
        Assert.Equal(0, await manager.CountRecoveryCodesAsync(user));
        var authCookie = success.Headers.GetValues("Set-Cookie").Single(cookie => cookie.StartsWith(".AspNetCore.Identity.Application="));
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", authCookie.Split(';')[0]);
        var admission = app.Services.GetRequiredService<IdentityAttemptAdmission>();
        for (var i = 0; i < IdentityAttemptAdmission.AttemptLimit; i++) admission.Admit(IPAddress.Parse("192.0.2.20"));
        using var manage = await client.GetAsync("/Identity/Account/Manage/TwoFactorAuthentication");
        Assert.Equal(HttpStatusCode.OK, manage.StatusCode);
        var management = new HtmlParser().ParseDocument(await manage.Content.ReadAsStringAsync());
        Assert.Contains("Δεν απομένουν κωδικοί ανάκτησης.", management.QuerySelector("main")?.TextContent);
        Assert.NotNull(management.QuerySelector("#language-open"));

        var manageToken = management.QuerySelector("#language-form input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        var manageCookie = manage.Headers.GetValues("Set-Cookie")
            .Single(cookie => cookie.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal)).Split(';')[0];
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", $"{authCookie.Split(';')[0]}; {manageCookie}");
        using var generate = await client.PostAsync("/Identity/Account/Manage/GenerateRecoveryCodes", Form(manageToken));
        Assert.Equal("/Identity/Account/Manage/ShowRecoveryCodes", generate.Headers.Location?.OriginalString);
        var resultCookie = generate.Headers.GetValues("Set-Cookie")
            .Single(cookie => cookie.StartsWith(".AspNetCore.Mvc.CookieTempDataProvider=", StringComparison.Ordinal)).Split(';')[0];
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", $"{authCookie.Split(';')[0]}; {resultCookie}");
        using var result = await client.GetAsync(generate.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var codes = new HtmlParser().ParseDocument(await result.Content.ReadAsStringAsync());
        Assert.Contains("Δημιουργήθηκαν νέοι κωδικοί ανάκτησης.", codes.QuerySelector("main .alert-success")?.TextContent);
        Assert.Equal(10, codes.QuerySelectorAll(".recovery-code").Length);
        Assert.Null(codes.QuerySelector("#language-open"));
        Assert.Contains(result.Headers.GetValues("Set-Cookie"), cookie =>
            cookie.StartsWith(".AspNetCore.Mvc.CookieTempDataProvider=;", StringComparison.Ordinal));
    }

    /// <summary>Preserves configured provider names and keys, with a localized, one-time callback error.</summary>
    [Fact]
    public async Task ExternalLoginPresentationUsesConfiguredProviderAndKeepsCallbackReturnUrl()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        const string provider = "FixtureProvider";
        const string displayName = "Fixture <Provider>";
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            IdentityRouteHost.WithStartupSettings(services);
            services.AddAuthentication().AddCookie(provider, displayName, _ => { });
        });
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el");
        client.DefaultRequestHeaders.Add("Cookie", IdentityRouteHost.Cookie(app.Services, user.Id));
        using var settings = await client.GetAsync("/Identity/Account/Manage/ExternalLogins");
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        var page = new HtmlParser().ParseDocument(await settings.Content.ReadAsStringAsync());
        var linkProvider = page.QuerySelector("#link-login-button-FixtureProvider");
        Assert.Equal(provider, linkProvider?.GetAttribute("value"));
        Assert.Equal(displayName, linkProvider?.TextContent);
        Assert.Equal("Σύνδεση μέσω του παρόχου «Fixture <Provider>»", linkProvider?.GetAttribute("title"));

        client.DefaultRequestHeaders.Remove("Cookie");
        using var callback = await client.GetAsync(
            "/Identity/Account/ExternalLogin?handler=Callback&returnUrl=%2Fdestination&remoteError=%3Cprovider-error%3E");
        Assert.Equal("/Identity/Account/Login?ReturnUrl=%2Fdestination", callback.Headers.Location?.OriginalString);
        var tempData = callback.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", tempData);
        using var login = await client.GetAsync(callback.Headers.Location);
        var refusal = new HtmlParser().ParseDocument(await login.Content.ReadAsStringAsync());
        Assert.Contains("Σφάλμα από τον εξωτερικό πάροχο: <provider-error>", refusal.QuerySelector("main .validation-summary-errors")?.TextContent);
        Assert.Null(refusal.QuerySelector("provider-error"));
        Assert.Null(refusal.QuerySelector("#language-open"));
    }

    /// <summary>Exercises token processing with preserved reset and email confirmation outcomes.</summary>
    [Fact]
    public async Task ResetPasswordAndConfirmation_AdmittedTokensKeepFrameworkBehavior()
    {
        var db = CreateDbContext();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), IdentityRouteHost.WithStartupSettings);
        using var scope = app.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = "token-user", Email = "user@example.test", DisplayName = "User", IsActive = true };
        Assert.True((await manager.CreateAsync(user, "Correct-Password1!")).Succeeded);
        using var client = app.GetTestClient();
        var token = await IdentityRouteHost.AntiforgeryAsync(client);
        var code = await manager.GeneratePasswordResetTokenAsync(user);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el");
        using var weak = await client.PostAsync("/Identity/Account/ResetPassword", Form(token,
            ("Input.Email", user.Email), ("Input.Code", code), ("Input.Password", "weak-password"),
            ("Input.ConfirmPassword", "weak-password")));
        Assert.Equal(HttpStatusCode.OK, weak.StatusCode);
        var weakForm = new HtmlParser().ParseDocument(await weak.Content.ReadAsStringAsync());
        Assert.Contains("Ο κωδικός πρόσβασης πρέπει να έχει τουλάχιστον ένα ψηφίο (0–9).",
            weakForm.QuerySelector("main .validation-summary-errors")?.TextContent);
        Assert.Null(weakForm.QuerySelector("#language-open"));
        Assert.True(await manager.CheckPasswordAsync(user, "Correct-Password1!"));

        using var reset = await client.PostAsync("/Identity/Account/ResetPassword", Form(token,
            ("Input.Email", user.Email), ("Input.Code", code), ("Input.Password", "Changed-Password2!"),
            ("Input.ConfirmPassword", "Changed-Password2!")));
        Assert.Equal("/Identity/Account/ResetPasswordConfirmation", reset.Headers.Location!.OriginalString);
        Assert.True(await manager.CheckPasswordAsync(user, "Changed-Password2!"));
        var confirmation = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await manager.GenerateEmailConfirmationTokenAsync(user)));
        using var confirmed = await client.GetAsync($"/Identity/Account/ConfirmEmail?userId={user.Id}&code={confirmation}");
        Assert.True(confirmed.StatusCode == HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync());
        var confirmationHtml = new HtmlParser().ParseDocument(await confirmed.Content.ReadAsStringAsync());
        Assert.Equal("el-GR", confirmationHtml.DocumentElement.GetAttribute("lang"));
        Assert.Contains("Η διεύθυνση email επιβεβαιώθηκε.", confirmationHtml.QuerySelector("main .alert-success")?.TextContent);
        Assert.Null(confirmationHtml.QuerySelector("#language-open"));
        Assert.True(await manager.IsEmailConfirmedAsync(user));
        using var missing = await client.GetAsync("/Identity/Account/ResetPassword");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("Για την επαναφορά του κωδικού πρόσβασης απαιτείται κωδικός επαναφοράς.",
            await missing.Content.ReadAsStringAsync());
    }

    /// <summary>Build a normal Identity form with its authentic antiforgery token.</summary>
    private static FormUrlEncodedContent Form(string token, params (string Key, string Value)[] fields) =>
        new(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value))
            .Append(new("__RequestVerificationToken", token)));
}
