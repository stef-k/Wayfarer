using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wayfarer.Controllers;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Controllers;

/// <summary>Exercises real application MVC discovery and routed Admin authorization.</summary>
public sealed class MvcActionBoundaryTests(ITestOutputHelper output) : TestBase
{
    private static readonly string[] HelperNames =
    [
        "HandleError", "LogAudit", "LogAction", "EnsureUserIsAuthorized",
        "ValidateModelState", "RedirectWithAlert", "GetUsersByRolesAsync"
    ];

    /// <summary>Inventories actual descriptors and prevents inherited helpers or raw user results.</summary>
    [Fact]
    public async Task ApplicationDescriptors_ExposeOnlyIntendedActions()
    {
        using var host = await StartHostAsync(CreateDbContext());
        var actions = host.Services.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items.OfType<ControllerActionDescriptor>().ToArray();
        var derived = actions.Where(a => typeof(BaseController).IsAssignableFrom(a.ControllerTypeInfo)).ToArray();
        var admin = actions.Where(a => a.RouteValues.TryGetValue("area", out var area) && area == "Admin").ToArray();
        foreach (var group in derived.Concat(admin).Distinct().GroupBy(a => a.ControllerName))
            output.WriteLine($"{group.Key}: {string.Join(", ", group.Select(a => a.ActionName).Distinct().Order())}");

        Assert.NotEmpty(derived);
        Assert.DoesNotContain(actions, a => a.ControllerTypeInfo.AsType() == typeof(BaseController));
        Assert.DoesNotContain(derived, a => HelperNames.Contains(a.MethodInfo.Name));
        Assert.DoesNotContain(derived, a => ContainsUserEntity(a.MethodInfo.ReturnType));
        Assert.Equal(new[] { "Error", "Index", "Privacy", "RegistrationClosed" },
            actions.Where(a => a.ControllerTypeInfo.AsType() == typeof(HomeController))
                .Select(a => a.ActionName).Order().ToArray());

        Assert.Equal(new[] { "ActivityType", "ApiToken", "AuditLogs", "Jobs", "Logs", "Settings", "TransportProfile", "Users" },
            admin.Select(a => a.ControllerName).Distinct().Order().ToArray());
        foreach (var action in admin)
        {
            Assert.DoesNotContain(action.EndpointMetadata, item => item is IAllowAnonymous);
            Assert.Contains(action.EndpointMetadata.OfType<IAuthorizeData>(), data => data.Roles == "Admin");
        }
    }

    /// <summary>Anonymous conventional requests cannot invoke any former helper on Home or Base.</summary>
    [Fact]
    public async Task HelperRoutes_ReturnNotFound()
    {
        using var host = await StartHostAsync(CreateDbContext());
        using var client = host.GetTestClient();
        foreach (var controller in new[] { "Home", "Base" })
        foreach (var helper in HelperNames.Append("GetUsersByRoles"))
        {
            using var response = await client.GetAsync($"/{controller}/{helper}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    /// <summary>A valid antiforgery request reaches the real delete action only for an Admin.</summary>
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("User", HttpStatusCode.Forbidden)]
    [InlineData("Manager", HttpStatusCode.Forbidden)]
    [InlineData("Admin", HttpStatusCode.Redirect)]
    public async Task ActivityTypeMutation_RequiresAdmin(string? role, HttpStatusCode expected)
    {
        var db = CreateDbContext();
        var activity = new ActivityType { Name = "Boundary test", Description = "Delete authorization" };
        db.ActivityTypes.Add(activity);
        await db.SaveChangesAsync();
        using var host = await StartHostAsync(db);
        using var client = host.GetTestClient();
        if (role != null) client.DefaultRequestHeaders.Add("X-Test-Role", role);
        using var tokenResponse = await client.GetAsync("/test-antiforgery");
        tokenResponse.EnsureSuccessStatusCode();
        var token = await tokenResponse.Content.ReadAsStringAsync();
        client.DefaultRequestHeaders.Add("Cookie", tokenResponse.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        using var response = await client.PostAsync($"/Admin/ActivityType/Delete/{activity.Id}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(role != "Admin", await db.ActivityTypes.FindAsync(activity.Id) != null);
        if (role == "Admin") Assert.Equal("/Admin/ActivityType", response.Headers.Location?.OriginalString);
    }

    /// <summary>Detects user entities even when wrapped in asynchronous or collection return types.</summary>
    private static bool ContainsUserEntity(Type type) => typeof(ApplicationUser).IsAssignableFrom(type)
        || type.GetGenericArguments().Any(ContainsUserEntity);

    /// <summary>Uses production application parts with real routing, authorization and antiforgery.</summary>
    private static Task<IHost> StartHostAsync(ApplicationDbContext db) => new HostBuilder()
        .ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddSingleton(db);
            services.AddControllersWithViews()
                .ConfigureApplicationPartManager(parts => parts.ApplicationParts.Clear())
                .AddApplicationPart(typeof(HomeController).Assembly);
            services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
            {
                // Return unambiguous statuses instead of following Identity UI redirects in this host.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = 401;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = 403;
                    return Task.CompletedTask;
                };
            });
        }).Configure(app =>
        {
            app.UseRouting();
            app.UseAuthentication();
            app.Use(async (context, next) =>
            {
                // Supply only the principal; production MVC policies still decide access.
                if (context.Request.Headers.TryGetValue("X-Test-Role", out var role))
                    context.User = BuildHttpContextWithUser("boundary-user", role.ToString()).User;
                if (context.Request.Path == "/test-antiforgery")
                {
                    var tokens = context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context);
                    await context.Response.WriteAsync(tokens.RequestToken!);
                    return;
                }
                await next(context);
            });
            app.UseAuthorization();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapAreaControllerRoute("admin", "Admin", "Admin/{controller=Home}/{action=Index}/{id?}");
                endpoints.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
            });
        })).StartAsync();
}
