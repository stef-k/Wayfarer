using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Wayfarer.Areas.Admin.Controllers;
using Wayfarer.Areas.Admin.Models;
using Wayfarer.Models;
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
        Assert.Contains("Minimum interval (seconds)", form, StringComparison.Ordinal);
        Assert.Contains("type=\"number\"", form, StringComparison.Ordinal);
        Assert.Contains("min=\"0.0\"", form, StringComparison.Ordinal);
        Assert.Contains("max=\"60.0\"", form, StringComparison.Ordinal);
        Assert.Contains("step=\"0.1\"", form, StringComparison.Ordinal);
    }

    /// <summary>Proves model validation accepts only declared personal-routing access values.</summary>
    [Theory]
    [InlineData(PersonalRoutingAccess.Disabled, true)]
    [InlineData(PersonalRoutingAccess.CredentialRequired, true)]
    [InlineData(PersonalRoutingAccess.CredentialFree, true)]
    [InlineData((PersonalRoutingAccess)999, false)]
    [InlineData((PersonalRoutingAccess)(-1), false)]
    public void EditModel_ValidatesPersonalRoutingAccess(PersonalRoutingAccess access, bool expectedValid)
    {
        var model = new RoutingProviderEditViewModel { PersonalRoutingAccess = access };
        var results = new List<ValidationResult>();

        Validator.TryValidateProperty(model.PersonalRoutingAccess,
            new ValidationContext(model) { MemberName = nameof(model.PersonalRoutingAccess) }, results);

        Assert.Equal(expectedValid, results.Count == 0);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Wayfarer.csproj"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
