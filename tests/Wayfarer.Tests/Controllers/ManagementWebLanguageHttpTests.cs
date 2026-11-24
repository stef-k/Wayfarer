using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;
using Browser = Wayfarer.Tests.Controllers.ManagerAccountAuthorizationPostgresTests.Browser;

namespace Wayfarer.Tests.Controllers;

/// <summary>Exercises the localized management pages with real Identity, antiforgery and database dependencies.</summary>
public sealed class ManagementWebLanguageHttpTests(PostgresMigrationTestFixture fixture)
    : TestBase, IClassFixture<PostgresMigrationTestFixture>
{
    /// <summary>Manager sees only permitted actions and the protected-delete alert uses the destination locale.</summary>
    [PostgresFact]
    public async Task ManagerProtectedAccount_RefusesDeletionInDestinationLocale()
    {
        await using var browser = await Browser.StartAsync(fixture, CreateTestDirectory(), "Manager");
        var target = await browser.UserAsync("User", protect: true);
        await browser.TokenAsync(target, ApiTokenService.ConnectionTokenName);
        var hidden = await browser.UserAsync("Admin");

        // The POST has no Greek preference; its redirect is rendered after the locale changes.
        using var refused = await browser.PostAsync("Users/DeleteConfirmed", ("id", target.Id));
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.EndsWith($"/Manager/Users/Delete/{target.Id}", refused.Headers.Location!.OriginalString);
        browser.Client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el-GR");
        var confirmation = await GetDocumentAsync(browser, refused.Headers.Location!.OriginalString);
        Assert.Contains("Ο λογαριασμός είναι προστατευμένος και δεν μπορεί να διαγραφεί.", confirmation.QuerySelector(".alert-danger")?.TextContent);

        var list = await GetDocumentAsync(browser, "/Manager/Users");
        var row = Assert.Single(list.QuerySelectorAll("tbody tr"), row => row.TextContent.Contains(target.UserName!));
        Assert.DoesNotContain(hidden.UserName!, list.QuerySelector("tbody")!.TextContent);
        Assert.Contains("Χρήστης", row.TextContent);
        Assert.NotNull(row.QuerySelector($"a[href='/Manager/Users/Edit/{target.Id}']"));
        Assert.NotNull(row.QuerySelector($"a[href='/Manager/Users/ChangePassword/{target.Id}']"));
        Assert.NotNull(row.QuerySelector($"a[href='/Manager/ApiToken/Index/{target.Id}']"));
        Assert.NotNull(row.QuerySelector("button[disabled]"));
        var create = await GetDocumentAsync(browser, "/Manager/Users/Create");
        var roles = create.QuerySelectorAll("#Role option").Skip(1);
        Assert.Equal("User", Assert.Single(roles).GetAttribute("value"));

        var tokens = await GetDocumentAsync(browser, $"/Manager/ApiToken/Index/{target.Id}");
        Assert.NotNull(tokens.QuerySelector("form[action='/Manager/ApiToken/Create']"));
        Assert.NotNull(tokens.QuerySelector("form[action='/Manager/ApiToken/Regenerate'] button[data-confirm-message]"));
        Assert.Null(tokens.QuerySelector("form[action='/Manager/ApiToken/Delete']"));

        browser.Client.DefaultRequestHeaders.AcceptLanguage.Clear();
        const string tokenName = "Fixture <token> & test";
        using var issued = await browser.PostAsync("ApiToken/Create", ("id", target.Id), ("name", tokenName));
        Assert.Equal(HttpStatusCode.Redirect, issued.StatusCode);
        browser.Client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el-GR");
        var tokenList = await GetDocumentAsync(browser, issued.Headers.Location!.OriginalString);
        var alert = tokenList.QuerySelector(".alert-success");
        Assert.Contains($"Δημιουργήθηκε το διακριτικό «{tokenName}» για τον χρήστη «{target.UserName}».", alert?.TextContent);
        Assert.Null(alert?.QuerySelector("token"));

        await using var fresh = fixture.CreateContext();
        Assert.True(await fresh.Users.AnyAsync(user => user.Id == target.Id && user.IsProtected));
        Assert.True(await fresh.ApiTokens.AnyAsync(token => token.UserId == target.Id));
    }

    /// <summary>Admin retains account actions and a referenced custom Transport Profile survives its localized refusal.</summary>
    [PostgresFact]
    public async Task AdminReferencedProfile_RefusesDeletionAndKeepsStoredValues()
    {
        await using var browser = await Browser.StartAsync(fixture, CreateTestDirectory(), "Admin");
        var target = await browser.UserAsync("Manager");
        browser.Client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el-GR");
        var list = await GetDocumentAsync(browser, $"/Admin/Users?search={target.UserName}");
        var row = Assert.Single(list.QuerySelectorAll("tbody tr"));
        Assert.Contains("Υπεύθυνος διαχείρισης", row.TextContent);
        Assert.NotNull(row.QuerySelector($"a[href='/Admin/Users/Delete/{target.Id}']"));
        Assert.NotNull(row.QuerySelector($"a[href='/Admin/ApiToken/Index/{target.Id}']"));
        var create = await GetDocumentAsync(browser, "/Admin/Users/Create");
        Assert.Equal("Διαχειριστής συστήματος", create.QuerySelector("#Role option[value=Admin]")?.TextContent.Trim());

        var profile = new TransportProfile
        {
            Id = Guid.NewGuid(), Key = "area-ten-test", Label = "Fixture & label",
            Category = "Fixture category", PlanningSpeedKmh = 17.5
        };
        var trip = new Trip { Id = Guid.NewGuid(), UserId = target.Id, Name = "Profile fixture" };
        var segment = new Segment { Id = Guid.NewGuid(), Trip = trip, UserId = target.Id, Mode = profile.Key, TransportProfileId = profile.Id };
        await using (var setup = fixture.CreateContext())
        {
            setup.AddRange(profile, trip, segment);
            await setup.SaveChangesAsync();
        }
        var profiles = await GetDocumentAsync(browser, $"/Admin/TransportProfile?search={profile.Key}");
        Assert.Contains("17,5", profiles.QuerySelector("tbody")!.TextContent);
        var edit = await GetDocumentAsync(browser, $"/Admin/TransportProfile/Edit/{profile.Id}");
        var speed = edit.QuerySelector("#PlanningSpeedKmh");
        Assert.Equal("17.5", speed?.GetAttribute("value"));
        Assert.Equal("Εισαγάγετε έγκυρη τιμή.", speed?.GetAttribute("data-val-number"));

        var route = $"/Admin/TransportProfile/Delete/{profile.Id}";
        using var refused = await browser.PostAsync(route, ("rowVersion", profile.RowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        var document = await new HtmlParser().ParseDocumentAsync(await refused.Content.ReadAsStringAsync());
        Assert.Contains("Δεν μπορείτε να διαγράψετε προφίλ που χρησιμοποιούνται.", document.QuerySelector(".validation-summary-errors")?.TextContent);
        Assert.Contains(profile.Label, document.QuerySelector("main")?.TextContent);
        Assert.Equal(profile.Key, document.QuerySelector("main code")?.TextContent);
        Assert.Null(document.QuerySelector("main form[method=post]"));

        await using var fresh = fixture.CreateContext();
        Assert.True(await fresh.Set<TransportProfile>().AnyAsync(item =>
            item.Id == profile.Id && item.Key == profile.Key && item.Label == profile.Label));
        Assert.True(await fresh.Segments.AnyAsync(item => item.Id == segment.Id && item.TransportProfileId == profile.Id));
    }

    /// <summary>Parses an admitted Greek document from the production route.</summary>
    private static async Task<IHtmlDocument> GetDocumentAsync(Browser browser, string route)
    {
        using var response = await browser.Client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync());
        Assert.Equal("el-GR", document.DocumentElement!.GetAttribute("lang"));
        return document;
    }
}
