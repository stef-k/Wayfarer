using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Locks removed routing endpoints, navigation, and EF authority types.</summary>
public sealed class LegacyRoutingRetirementContractTests : TestBase
{
    [Theory]
    [InlineData("GET", "/User/RoutingSettings")]
    [InlineData("POST", "/User/RoutingSettings/Save")]
    [InlineData("GET", "/Admin/RoutingProvider")]
    [InlineData("POST", "/Admin/RoutingProvider/Activate")]
    [InlineData("GET", "/User/ApiToken")]
    [InlineData("POST", "/User/ApiToken/Create")]
    [InlineData("POST", "/User/ApiToken/Regenerate")]
    [InlineData("GET", "/User/ApiToken/Delete/1")]
    [InlineData("POST", "/User/ApiToken/Delete")]
    [InlineData("POST", "/User/ApiToken/StoreThirdPartyToken")]
    public async Task RemovedEndpointsReturnNotFound(string method, string path)
    {
        using var host = await new HostBuilder().ConfigureWebHost(webHost => webHost
            .UseTestServer()
            .ConfigureServices(services => services.AddControllersWithViews()
                .AddApplicationPart(typeof(ApplicationDbContext).Assembly))
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapControllerRoute(
                    "areas", "{area:exists}/{controller=Home}/{action=Index}/{id?}"));
            })).StartAsync();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await host.GetTestClient().SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void NavigationAndModelContainNoLegacyRoutingAuthority()
    {
        var root = FindRepositoryRoot();
        var adminNavigation = File.ReadAllText(Path.Combine(root, "Views", "Shared", "_AdminNav.cshtml"));
        var userSettings = File.ReadAllText(Path.Combine(root, "Areas", "User", "Views", "Settings", "Index.cshtml"));

        Assert.DoesNotContain("RoutingProvider", adminNavigation, StringComparison.Ordinal);
        Assert.DoesNotContain("RoutingSettings", userSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiToken", userSettings, StringComparison.Ordinal);
        Assert.Contains("LocationProviderSettings", userSettings, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "Areas", "User", "Views", "ApiToken", "Index.cshtml")));
        Assert.False(File.Exists(Path.Combine(root, "Areas", "User", "Views", "ApiToken", "Delete.cshtml")));
        using var db = CreateDbContext();
        Assert.Null(db.Model.FindEntityType("Wayfarer.Models.RoutingProviderConfiguration"));
        Assert.Null(db.Model.FindEntityType("Wayfarer.Models.UserRoutingConfiguration"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Wayfarer.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
