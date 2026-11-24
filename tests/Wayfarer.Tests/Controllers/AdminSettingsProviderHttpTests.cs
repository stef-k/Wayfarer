using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Exercises provider recovery through the authenticated Settings page.</summary>
public sealed class AdminSettingsProviderHttpTests : TestBase
{
    /// <summary>Invalid Custom limits retain the draft and leave the saved settings intact.</summary>
    [Fact]
    public async Task Update_InvalidCustomLimitsShowsGreekRecoveryAndRetainsDraft()
    {
        var db = CreateDbContext();
        var existing = new ApplicationSettings();
        await using var app = await StartSettingsHostAsync(db, existing);
        var saved = db.Entry(existing).CurrentValues.Clone();
        using var client = CreateGreekAdminClient(app);
        var auth = client.DefaultRequestHeaders.GetValues("Cookie").Single();
        var token = await IdentityRouteHost.AntiforgeryAsync(client);
        var csrf = client.DefaultRequestHeaders.GetValues("Cookie").Single();
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", $"{auth}; {csrf}");

        var values = new Dictionary<string, string>
        {
            ["TileProviderUrlTemplate"] = "https://tiles.example.test/{z}/{x}/{y}.png",
            ["TileProviderAttribution"] = "Example attribution",
            ["TileProviderAdvancedLimitsEnabled"] = "true",
            ["TileProviderSustainedRequestsPerSecond"] = "21",
            ["TileProviderBurstCapacity"] = "2",
            ["TileProviderMaxConcurrency"] = "6",
            ["TileProviderFallbackBaseDelayMs"] = "5000",
            ["TileProviderFallbackDelayCapSeconds"] = "1",
            ["TileProviderMaxIndividualWaitSeconds"] = "120",
            ["TileProviderTotalRetryCeilingSeconds"] = "5",
            ["VisitedAccuracyMultiplier"] = "1.7"
        };
        using var form = new FormUrlEncodedContent(values.Concat(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["TileProviderKey"] = "custom",
            ["TileTrafficMode"] = "2"
        }));
        using var response = await client.PostAsync("/Admin/Settings/Update", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync());

        Assert.Equal("el-GR", page.DocumentElement.GetAttribute("lang"));
        var field = Assert.Single(page.QuerySelectorAll("[data-valmsg-for=TileProviderAdvancedLimitsEnabled]"));
        Assert.Equal("Ελέγξτε τα όρια της συμφωνίας παρόχου και δοκιμάστε ξανά. " +
            "Το όριο αιχμής πρέπει να είναι τουλάχιστον ίσο με το μέγιστο πλήθος ταυτόχρονων αιτημάτων. " +
            "Το όριο καθυστέρησης επανάληψης πρέπει να είναι τουλάχιστον ίσο με την αρχική καθυστέρηση. " +
            "Το συνολικό όριο επαναλήψεων πρέπει να είναι τουλάχιστον ίσο με τη μέγιστη αναμονή ανά απόκριση.",
            field.TextContent.Trim());
        Assert.Equal("Εισαγάγετε τιμή μεταξύ 1 και 20.",
            page.QuerySelector("[data-valmsg-for=TileProviderSustainedRequestsPerSecond]")?.TextContent.Trim());
        Assert.Equal("custom", page.QuerySelector("#TileProviderKey option[selected]")?.GetAttribute("value"));
        Assert.Equal("2", page.QuerySelector("#TileTrafficMode option[selected]")?.GetAttribute("value"));
        foreach (var (fieldName, value) in values)
            Assert.Equal(value, page.QuerySelector($"#{fieldName}")?.GetAttribute("value"));

        db.ChangeTracker.Clear();
        var persisted = db.Entry(await db.ApplicationSettings.SingleAsync()).CurrentValues;
        foreach (var property in saved.Properties)
            Assert.Equal(saved[property], persisted[property]);

        var warning = BlockedProviderWarning(page);
        var source = CompatibilitySource(page);
        Assert.Multiple(
            () => Assert.Contains("Διορθώστε τα όρια κίνησης πριν από την ενεργοποίηση.", warning),
            () => Assert.DoesNotContain("Επιλέξτε OSM Standard", warning),
            () => Assert.Contains("Δικλίδες ασφαλείας του Wayfarer", source),
            () => Assert.DoesNotContain("Wayfarer safeguards", source));
    }

    /// <summary>A blocked provider still directs the administrator to a compatible provider and keeps its source URL.</summary>
    [Fact]
    public async Task Index_BlockedProviderShowsGreekSelectionGuidanceAndExternalSource()
    {
        var db = CreateDbContext();
        await using var app = await StartSettingsHostAsync(db, new ApplicationSettings
        {
            TileProviderKey = "thunderforest-cycle"
        });
        using var client = CreateGreekAdminClient(app);

        using var response = await client.GetAsync("/Admin/Settings");
        response.EnsureSuccessStatusCode();
        var page = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync());

        Assert.Contains("Επιλέξτε OSM Standard, OpenTopoMap ή συμβατό προσαρμοσμένο πάροχο.", BlockedProviderWarning(page));
        Assert.Contains("https://www.thunderforest.com/terms/", CompatibilitySource(page));
    }

    /// <summary>Seeds the specified settings and enables the incumbent Settings controller dependencies.</summary>
    private Task<WebApplication> StartSettingsHostAsync(ApplicationDbContext db, ApplicationSettings settings)
    {
        db.Users.Add(TestDataFixtures.CreateUser(id: "provider-admin", username: "provider-admin"));
        db.ApplicationSettings.Add(settings);
        db.SaveChanges();
        return IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddSingleton(Mock.Of<IApplicationSettingsService>(service => service.GetSettings() == settings));
            services.AddSingleton<TileCacheStorage>();
        });
    }

    /// <summary>Creates an authenticated administrator's Greek request client.</summary>
    private static HttpClient CreateGreekAdminClient(WebApplication app)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el-GR");
        client.DefaultRequestHeaders.Add("Cookie", IdentityRouteHost.Cookie(app.Services, "provider-admin", "Admin"));
        return client;
    }

    /// <summary>Finds the visible compatibility warning among page alerts.</summary>
    private static string BlockedProviderWarning(IDocument page) =>
        Assert.Single(page.QuerySelectorAll(".alert.alert-danger[role=alert]"),
            element => element.TextContent.Contains("Ο πάροχος έχει αποκλειστεί")).TextContent;

    /// <summary>Finds the source in the visible compatibility summary.</summary>
    private static string CompatibilitySource(IDocument page) =>
        Assert.Single(page.QuerySelectorAll(".alert.alert-secondary"),
            element => element.TextContent.Contains("Επίσημη πηγή")).TextContent;
}
