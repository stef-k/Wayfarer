using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class ApiLocationProviderCreatePostgresTests
{
    /// <summary>Web manual intent suppresses actual qualified provider admission; replay cannot charge or overwrite.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WebCreationSharesProviderWinnerRules(bool manualAddress)
    {
        var protection = new EphemeralDataProtectionProvider();
        var (owner, _) = await SeedAsync("geoapify", protection);
        using var handler = new ControlledProvider();
        await WithHostAsync(protection, handler, [], async client =>
        {
            await WebCookieAsync(client, owner);
            var initial = await client.GetStringAsync("/User/Location/Create");
            var values = new Dictionary<string, string> { ["CreateKey"] = Input(initial, "CreateKey"), ["CreateIntent"] = Input(initial, "CreateIntent"),
                ["__RequestVerificationToken"] = Input(initial, "__RequestVerificationToken"), ["Latitude"] = "10", ["Longitude"] = "20",
                ["LocalTimestamp"] = "2026-11-08T12:00:00", ["Notes"] = "first", ["FullAddress"] = manualAddress ? " Only manual full address " : "" };
            using (var first = await client.PostAsync("/User/Location/Create", new FormUrlEncodedContent(values))) Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            values["Notes"] = "newer";
            using (var replay = await client.PostAsync("/User/Location/Create", new FormUrlEncodedContent(values)))
            { Assert.Equal(HttpStatusCode.OK, replay.StatusCode); Assert.Contains("current form changes were not applied", await replay.Content.ReadAsStringAsync()); }
            await using var db = fixture.CreateContext();
            var row = await db.Locations.AsNoTracking().SingleAsync(row => row.UserId == owner);
            Assert.Contains("first", row.Notes); Assert.Null(row.OriginalCreateAddressToken);
            Assert.Equal(manualAddress ? 0 : 1, handler.Calls); Assert.Equal(manualAddress ? 0 : 1, await AdmissionCountAsync(owner, "geoapify"));
            if (manualAddress) { Assert.Equal("Only manual full address", row.FullAddress); Assert.Null(row.ReverseGeocodingProvider); }
        });

        static string Input(string html, string name) => WebUtility.HtmlDecode(Regex.Match(
            Regex.Match(html, "<input[^>]*name=\"" + name + "\"[^>]*>").Value, "value=\"([^\"]*)\"").Groups[1].Value);
    }
}
