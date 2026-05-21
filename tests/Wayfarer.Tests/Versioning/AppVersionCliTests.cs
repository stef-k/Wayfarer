using FluentAssertions;
using Wayfarer.CommandLine;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Versioning;

public class AppVersionCliTests
{
    [Fact]
    public void TryHandle_VersionCommand_WritesExactVersionLine()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var handled = AppVersionCli.TryHandle(
            new[] { "version" },
            new StubAppVersionProvider("1.4.0"),
            output,
            error,
            out var exitCode);

        handled.Should().BeTrue();
        exitCode.Should().Be(0);
        output.ToString().Should().Be($"Wayfarer 1.4.0{Environment.NewLine}");
        error.ToString().Should().BeEmpty();
    }

    [Fact]
    public void TryHandle_VersionCommand_DoesNotRequireWebHostOrDatabaseStartup()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var handled = AppVersionCli.TryHandle(
            new[] { "version" },
            new StubAppVersionProvider("1.4.0"),
            output,
            error,
            out var exitCode);

        handled.Should().BeTrue();
        exitCode.Should().Be(0);
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
