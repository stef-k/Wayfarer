using FluentAssertions;
using Wayfarer.Services;

namespace Wayfarer.Tests.Versioning;

public class AppVersionDisplayTests
{
    [Fact]
    public void FooterText_RendersSharedLayoutVersionText()
    {
        AppVersionDisplay.FooterText(new StubAppVersionProvider("1.4.0"))
            .Should()
            .Be("Wayfarer v1.4.0");
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
            "Views",
            "Shared",
            "_Layout.cshtml"));
        var layout = File.ReadAllText(layoutPath);

        layout.Should().Contain("@inject IAppVersionProvider AppVersionProvider");
        layout.Should().Contain("@AppVersionDisplay.FooterText(AppVersionProvider)");
        layout.Should().NotContain("Wayfarer v1.4.0");
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
