using System.Text.RegularExpressions;
using Xunit;

namespace Wayfarer.Tests.Tools;

/// <summary>Guards the supported production deployment script's EF migration ownership.</summary>
public sealed class DeploymentScriptTests
{
    [Fact]
    public void EfMigrationCommands_SelectApplicationDbContext()
    {
        var script = File.ReadAllText(RepositoryFile("deployment", "deploy.sh"))
            .Replace("\\\r\n", " ", StringComparison.Ordinal)
            .Replace("\\\n", " ", StringComparison.Ordinal);
        var migrationCommands = Regex.Matches(script,
            @"(?m)^[^#\r\n]*\bdotnet\s+ef\s+database\s+update\b[^\r\n]*$");

        Assert.NotEmpty(migrationCommands);
        Assert.All(migrationCommands, command => Assert.Contains(
            "--context Wayfarer.Models.ApplicationDbContext", command.Value, StringComparison.Ordinal));
    }

    private static string RepositoryFile(params string[] parts) => Path.GetFullPath(
        Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", .. parts]));
}
