using FluentAssertions;
using Wayfarer.CommandLine;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Versioning;

public class AppVersionCliTests
{
    private static readonly string TopLevelHelp = string.Join(
        Environment.NewLine,
        "Wayfarer CLI",
        "",
        "Usage:",
        "  Wayfarer <command> [options]",
        "",
        "Commands:",
        "  version                         Print the compiled Wayfarer version.",
        "  reset-password <user> <pass>    Reset a user's password.",
        "  help                            Show this help text.",
        "");

    private static readonly string VersionHelp = string.Join(
        Environment.NewLine,
        "Usage:",
        "  Wayfarer version",
        "",
        "Prints the compiled Wayfarer version and exits before web host startup.",
        "");

    private static readonly string ResetPasswordHelp = string.Join(
        Environment.NewLine,
        "Usage:",
        "  Wayfarer reset-password <user> <pass>",
        "",
        "Resets a user's password using the configured application database.",
        "");

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public void TryHandle_TopLevelHelpCommands_WriteExactHelpAndExitZero(string command)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var handled = AppVersionCli.TryHandle(
            new[] { command },
            new StubAppVersionProvider("1.4.0"),
            output,
            error,
            out var exitCode);

        handled.Should().BeTrue();
        exitCode.Should().Be(0);
        NormalizeLineEndings(output.ToString()).Should().Be(NormalizeLineEndings(TopLevelHelp));
        error.ToString().Should().BeEmpty();
    }

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

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void TryHandle_VersionHelpCommands_WriteExactHelpAndExitZero(string option)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var handled = AppVersionCli.TryHandle(
            new[] { "version", option },
            new StubAppVersionProvider("1.4.0"),
            output,
            error,
            out var exitCode);

        handled.Should().BeTrue();
        exitCode.Should().Be(0);
        NormalizeLineEndings(output.ToString()).Should().Be(NormalizeLineEndings(VersionHelp));
        error.ToString().Should().BeEmpty();
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void TryHandle_ResetPasswordHelpCommands_WriteExactHelpAndExitZero(string option)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var handled = AppVersionCli.TryHandle(
            new[] { "reset-password", option },
            new StubAppVersionProvider("1.4.0"),
            output,
            error,
            out var exitCode);

        handled.Should().BeTrue();
        exitCode.Should().Be(0);
        NormalizeLineEndings(output.ToString()).Should().Be(NormalizeLineEndings(ResetPasswordHelp));
        error.ToString().Should().BeEmpty();
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("version")]
    [InlineData("version", "--help")]
    [InlineData("version", "-h")]
    [InlineData("reset-password", "--help")]
    [InlineData("reset-password", "-h")]
    public void TryHandle_HostFreeCommands_AreHandledByPureCliSeam(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var handled = AppVersionCli.TryHandle(
            args,
            new StubAppVersionProvider("1.4.0"),
            output,
            error,
            out var exitCode);

        handled.Should().BeTrue();
        exitCode.Should().Be(0);
    }

    [Fact]
    public void TryHandle_UnknownCommand_KeepsCurrentFallThroughBehavior()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var handled = AppVersionCli.TryHandle(
            new[] { "unknown" },
            new StubAppVersionProvider("1.4.0"),
            output,
            error,
            out var exitCode);

        handled.Should().BeFalse();
        exitCode.Should().Be(0);
        output.ToString().Should().BeEmpty();
        error.ToString().Should().BeEmpty();
    }

    [Fact]
    public void TryHandle_NormalResetPasswordCommand_RemainsUnhandled()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var handled = AppVersionCli.TryHandle(
            new[] { "reset-password", "user", "pass" },
            new StubAppVersionProvider("1.4.0"),
            output,
            error,
            out var exitCode);

        handled.Should().BeFalse();
        exitCode.Should().Be(0);
        output.ToString().Should().BeEmpty();
        error.ToString().Should().BeEmpty();
    }

    private sealed class StubAppVersionProvider : IAppVersionProvider
    {
        public StubAppVersionProvider(string version)
        {
            Version = version;
        }

        public string Version { get; }
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.ReplaceLineEndings("\n");
    }
}
