using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Models;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>
/// Base class for all tests providing common database and controller setup functionality.
/// </summary>
public abstract class TestBase : IDisposable
{
    private readonly List<ApplicationDbContext> _contexts = new();

    /// <summary>
    /// Creates a new in-memory database context for testing.
    /// The context is automatically disposed when the test completes.
    /// </summary>
    /// <returns>A new ApplicationDbContext instance using an in-memory database.</returns>
    protected ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var context = new ApplicationDbContext(options, new ServiceCollection().BuildServiceProvider());
        _contexts.Add(context);
        return context;
    }

    /// <summary>
    /// Creates a ClaimsPrincipal for a user with the specified user ID.
    /// </summary>
    /// <param name="userId">The user ID to include in the claims.</param>
    /// <returns>A ClaimsPrincipal with the user ID claim.</returns>
    protected static ClaimsPrincipal CreateUserPrincipal(string userId)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        var identity = new ClaimsIdentity(claims, "Test");
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Creates an HttpContext with optional bearer token authentication.
    /// </summary>
    /// <param name="bearerToken">The bearer token to include in the Authorization header. If null, no token is added.</param>
    /// <returns>A DefaultHttpContext with the specified authentication.</returns>
    protected static HttpContext CreateHttpContext(string? bearerToken = null)
    {
        var context = new DefaultHttpContext();
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            context.Request.Headers["Authorization"] = $"Bearer {bearerToken}";
        }
        return context;
    }

    /// <summary>
    /// Creates an HttpContext with a user principal for claims-based authentication.
    /// </summary>
    /// <param name="userId">The user ID to set as the authenticated user.</param>
    /// <returns>A DefaultHttpContext with the user principal set.</returns>
    protected static HttpContext CreateHttpContextWithUser(string userId)
    {
        var context = new DefaultHttpContext
        {
            User = CreateUserPrincipal(userId)
        };
        return context;
    }

    /// <summary>
    /// Helper to create an HttpContext with user and optional role (used by various controller tests).
    /// </summary>
    protected static DefaultHttpContext BuildHttpContextWithUser(string userId, string role = "User")
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Role, role)
        };
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
        };
    }

    /// <summary>
    /// Configures a controller with the specified user ID for authentication.
    /// </summary>
    /// <typeparam name="T">The type of controller.</typeparam>
    /// <param name="controller">The controller instance to configure.</param>
    /// <param name="userId">The user ID to set as the authenticated user.</param>
    /// <returns>The configured controller.</returns>
    protected static T ConfigureControllerWithUser<T>(T controller, string userId) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = CreateHttpContextWithUser(userId)
        };
        return controller;
    }

    /// <summary>
    /// Configures a controller with a specific HttpContext.
    /// </summary>
    /// <typeparam name="T">The type of controller.</typeparam>
    /// <param name="controller">The controller instance to configure.</param>
    /// <param name="httpContext">The HttpContext to use.</param>
    /// <returns>The configured controller.</returns>
    protected static T ConfigureControllerWithContext<T>(T controller, HttpContext httpContext) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
        return controller;
    }

    /// <summary>
    /// Disposes all database contexts created during the test.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes managed resources.
    /// </summary>
    /// <param name="disposing">True if disposing managed resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var context in _contexts)
            {
                context.Dispose();
            }
            _contexts.Clear();
        }
    }
}
