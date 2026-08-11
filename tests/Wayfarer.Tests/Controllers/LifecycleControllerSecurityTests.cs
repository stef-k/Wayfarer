using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Areas.Api.Controllers;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Verifies lifecycle controller endpoints retain their established security filters.</summary>
public sealed class LifecycleControllerSecurityTests
{
    /// <summary>Requires antiforgery on every Trip Editor Place and Region lifecycle mutation.</summary>
    [Fact]
    public void TripEditorLifecycleMutations_RetainAntiforgery()
    {
        foreach (var methodName in new[] { "UpdatePlace", "DeletePlace", "UpdateRegion", "DeleteRegion" })
            Assert.NotEmpty(typeof(TripEditorController).GetMethod(methodName)!
                .GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
    }

    /// <summary>Requires the authenticated User role at the Trip Editor controller boundary.</summary>
    [Fact]
    public void TripEditorLifecycleControllers_RetainAuthenticatedUserRole()
    {
        var authorization = Assert.Single(typeof(TripEditorController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal("User", authorization.Roles);
    }

    /// <summary>Ensures legacy token endpoints do not acquire cookie-antiforgery filters.</summary>
    [Fact]
    public void LegacyLifecycleDeletes_RetainTokenApiFilterShape()
    {
        foreach (var methodName in new[] { "DeletePlace", "DeleteRegion" })
        {
            var method = typeof(TripsController).GetMethod(methodName)!;
            Assert.Empty(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
            Assert.NotEmpty(method.GetCustomAttributes(typeof(HttpDeleteAttribute), true));
        }
    }
}
