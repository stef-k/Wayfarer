using FluentAssertions;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Versioning;

public class AppVersionDisplayTests
{
    [Fact]
    public void FooterText_RendersSharedLayoutVersionText()
    {
        AppVersionDisplay.FooterText(new StubAppVersionProvider("1.4.1"))
            .Should()
            .Be("Wayfarer v1.4.1");
    }

    [Fact]
    public void SharedLayout_UsesProviderBackedFooterHelper()
    {
        var layoutPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "Views",
            "Shared",
            "_Layout.cshtml"));
        var layout = File.ReadAllText(layoutPath);

        layout.Should().Contain("@inject IAppVersionProvider AppVersionProvider");
        layout.Should().Contain("@AppVersionDisplay.FooterText(AppVersionProvider)");
        layout.Should().Contain("&copy; @DateTime.UtcNow.Year - @AppVersionDisplay.FooterText(AppVersionProvider) by");
        layout.Should().NotContain("&copy; 2025");
        layout.Should().NotContain("Privacy</a> -");
        layout.Should().NotContain("Wayfarer v1.4.1");
    }

    private sealed class StubAppVersionProvider : IAppVersionProvider
    {
        public StubAppVersionProvider(string version)
        {
            Version = version;
        }

        public string Version { get; }
    }
}
