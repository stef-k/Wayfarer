using Xunit;

namespace Wayfarer.Tests.Views;

/// <summary>Guards the responsive Location address disclosure and shared project link.</summary>
public sealed class LocationEditViewContractTests
{
    [Fact]
    public void EditViewUsesResponsiveAddressDisclosureWithoutNativeAlerts()
    {
        var source = Read("Areas", "User", "Views", "Location", "Edit.cshtml");

        Assert.Contains("<details class=\"border rounded my-2\"", source);
        Assert.Contains("col-12 col-lg-8", source);
        Assert.Contains("col-12 col-md-6", source);
        Assert.Contains("expandAddress ? \"open\" : null", source);
        Assert.Contains("string.Join(\", \"", source);
        Assert.Contains("resolved to local-area level", source);
        foreach (var field in new[] { "FullAddress", "Address", "StreetName", "AddressNumber",
                     "PostCode", "Place", "Region", "Country" })
            Assert.Contains($"asp-for=\"{field}\"", source);
        Assert.DoesNotContain("alert(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedFooterLinksSafelyToProject()
    {
        var source = Read("Views", "Shared", "_Layout.cshtml");

        Assert.Contains("href=\"https://github.com/stef-k/Wayfarer\"", source);
        Assert.Contains("target=\"_blank\" rel=\"noopener noreferrer\"", source);
        Assert.Contains("aria-label=\"Wayfarer source code on GitHub\"", source);
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.GetFullPath(
        Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", .. parts])));
}
