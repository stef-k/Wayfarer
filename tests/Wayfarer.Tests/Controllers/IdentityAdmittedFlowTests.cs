using System.Net;
using System.Text;
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
    [Fact]
    public async Task PasswordSpraying_UsesOneBudget_WhileAccountLockoutStillApplies()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings());
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
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

    [Fact]
    public async Task TwoFactorAndRecovery_KeepPendingCookieAndSuccessfulRedirect()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings());
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
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
        using var invalid = await client.PostAsync("/Identity/Account/LoginWith2fa",
            Form(token, ("Input.TwoFactorCode", "xxxxxx")));
        Assert.True(invalid.StatusCode == HttpStatusCode.OK, await invalid.Content.ReadAsStringAsync());
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
    }

    [Fact]
    public async Task ResetPasswordAndConfirmation_AdmittedTokensKeepFrameworkBehavior()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings());
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var scope = app.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = "token-user", Email = "user@example.test", DisplayName = "User", IsActive = true };
        Assert.True((await manager.CreateAsync(user, "Correct-Password1!")).Succeeded);
        using var client = app.GetTestClient();
        var token = await IdentityRouteHost.AntiforgeryAsync(client);
        var code = await manager.GeneratePasswordResetTokenAsync(user);
        using var reset = await client.PostAsync("/Identity/Account/ResetPassword", Form(token,
            ("Input.Email", user.Email), ("Input.Code", code), ("Input.Password", "Changed-Password2!"),
            ("Input.ConfirmPassword", "Changed-Password2!")));
        Assert.Equal("/Identity/Account/ResetPasswordConfirmation", reset.Headers.Location!.OriginalString);
        Assert.True(await manager.CheckPasswordAsync(user, "Changed-Password2!"));
        var confirmation = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await manager.GenerateEmailConfirmationTokenAsync(user)));
        using var confirmed = await client.GetAsync($"/Identity/Account/ConfirmEmail?userId={user.Id}&code={confirmation}");
        Assert.True(confirmed.StatusCode == HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync());
        Assert.True(await manager.IsEmailConfirmedAsync(user));
        using var missing = await client.GetAsync("/Identity/Account/ResetPassword");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    /// <summary>Build a normal Identity form with its authentic antiforgery token.</summary>
    private static FormUrlEncodedContent Form(string token, params (string Key, string Value)[] fields) =>
        new(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value))
            .Append(new("__RequestVerificationToken", token)));
}
