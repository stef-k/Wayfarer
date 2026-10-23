using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>History text shares the existing fenced provider publication boundary.</summary>
public sealed partial class GeoapifyBackfillConcurrencyPostgresTests
{
    [PostgresTheory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothPublicationBranchesDeriveFromCurrentNotesAndRetainedFields(bool repair)
    {
        var owner = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        if (repair) await SeedIncompleteRepairAsync(owner.Id, protection);
        else await SeedAsync(owner.Id, null, protection);
        var handler = new CoordinatedHandler(owner.Id, null);
        var run = Service(protection, handler).RunAsync(owner.Id);
        await handler.FirstUserRequestEntered.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var edit = fixture.CreateContext())
        {
            var row = await edit.Locations.SingleAsync(item => item.UserId == owner.Id);
            row.Notes = "<p>current café <b>words</b></p>";
            await edit.SaveChangesAsync();
        }
        handler.Release();
        Assert.Equal(1, (await run.WaitAsync(TimeSpan.FromSeconds(10))).Succeeded);
        await using var verify = fixture.CreateContext();
        var saved = await verify.Locations.SingleAsync(item => item.UserId == owner.Id);
        var text = saved.HistorySearchText;
        Assert.Contains("CURRENT CAFE WORDS", text);
        if (repair) Assert.Contains("KEEP THIS ADDRESS", text);
        Assert.Contains("ALEXANDROUPOLIS", text);
        LocationHistoryDerivation.Apply(saved, false, true);
        Assert.Equal(text, saved.HistorySearchText);
    }
}
