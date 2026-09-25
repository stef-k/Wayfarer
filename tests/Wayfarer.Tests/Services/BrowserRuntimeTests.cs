using Microsoft.Playwright;
using Moq;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves browser operations use the supplied runtime and fail without an installer fallback.</summary>
public sealed class BrowserRuntimeTests
{
    /// <summary>Existing capture options reach the normal version-matched Chromium launcher unchanged.</summary>
    [Fact]
    public async Task LaunchUsesSuppliedRuntimeAndOptions()
    {
        var options = new BrowserTypeLaunchOptions { Headless = true, Args = new[] { "--ignore-certificate-errors" } };
        var browser = Mock.Of<IBrowser>();
        var chromium = new Mock<IBrowserType>(MockBehavior.Strict);
        chromium.Setup(value => value.LaunchAsync(options)).ReturnsAsync(browser);
        var playwright = new Mock<IPlaywright>(MockBehavior.Strict);
        playwright.SetupGet(value => value.Chromium).Returns(chromium.Object);

        Assert.Same(browser, await BrowserRuntime.LaunchAsync(playwright.Object, options));
        chromium.Verify(value => value.LaunchAsync(options), Times.Once);
    }

    /// <summary>A missing executable or OS library fails once with provisioning guidance and original evidence.</summary>
    [Theory]
    [InlineData("Executable doesn't exist")]
    [InlineData("libasound.so.2: cannot open shared object file")]
    public async Task MissingRuntimeFailsWithoutRetry(string failure)
    {
        var original = new PlaywrightException(failure);
        var chromium = new Mock<IBrowserType>(MockBehavior.Strict);
        chromium.Setup(value => value.LaunchAsync(It.IsAny<BrowserTypeLaunchOptions>())).ThrowsAsync(original);
        var playwright = new Mock<IPlaywright>(MockBehavior.Strict);
        playwright.SetupGet(value => value.Chromium).Returns(chromium.Object);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BrowserRuntime.LaunchAsync(playwright.Object, new BrowserTypeLaunchOptions()));

        Assert.Same(original, exception.InnerException);
        Assert.Contains("does not install browsers at runtime", exception.Message);
        chromium.Verify(value => value.LaunchAsync(It.IsAny<BrowserTypeLaunchOptions>()), Times.Once);
        chromium.VerifyNoOtherCalls();
    }
}
