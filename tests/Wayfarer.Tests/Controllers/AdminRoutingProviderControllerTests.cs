using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using Wayfarer.Areas.Admin.Controllers;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Verifies the focused external-routing Admin authorization and Razor redaction contract.</summary>
public sealed class AdminRoutingProviderControllerTests
{
    [Fact]
    public void Mutations_RequireAdminAndAntiforgery()
    {
        var authorization = Assert.Single(typeof(RoutingProviderController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal("Admin", authorization.Roles);
        foreach (var method in typeof(RoutingProviderController).GetMethods()
                     .Where(method => method.GetCustomAttributes<HttpPostAttribute>().Any()))
            Assert.NotEmpty(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
    }

    [Fact]
    public void RazorForms_MaskCredentialAndNeverReferenceCiphertext()
    {
        var root = FindRepositoryRoot();
        var form = File.ReadAllText(Path.Combine(root, "Areas", "Admin", "Views", "RoutingProvider", "_Form.cshtml"));
        var index = File.ReadAllText(Path.Combine(root, "Areas", "Admin", "Views", "RoutingProvider", "Index.cshtml"));

        Assert.Contains("type=\"password\"", form, StringComparison.Ordinal);
        Assert.Contains("Leave blank to preserve", form, StringComparison.Ordinal);
        Assert.DoesNotContain("CredentialCiphertext", form + index, StringComparison.Ordinal);
        Assert.DoesNotContain("BaseEndpoint", index, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Wayfarer.csproj"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
