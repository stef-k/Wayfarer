using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Missing and closed settings deny registration before any account dependency is used.</summary>
public class RegistrationServiceTests : TestBase
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Policy_FailsClosed_AndHandlersStopBeforeAccountWork(bool? open)
    {
        var db = CreateDbContext();
        if (open.HasValue)
        {
            db.ApplicationSettings.Add(new ApplicationSettings { IsRegistrationOpen = open.Value });
            await db.SaveChangesAsync();
        }
        var policy = new RegistrationService(db);
        Assert.Equal(open == true, policy.IsRegistrationOpen());
        if (open == true) return; // Open GET/POST side effects are exercised through real routed pages.

        // Null account services deliberately make any lookup/mutation beyond the policy fail.
        var page = new RegisterModel(null!, null!, null!, null!, null!, policy);
        Assert.Equal("/Home/RegistrationClosed", Assert.IsType<RedirectResult>(await page.OnGetAsync()).Url);
        Assert.Equal("/Home/RegistrationClosed", Assert.IsType<RedirectResult>(await page.OnPostAsync()).Url);
        Assert.Empty(db.Users);
        Assert.Empty(db.UserRoles);
        Assert.Empty(db.ApiTokens);
    }
}
