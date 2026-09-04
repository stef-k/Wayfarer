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
        Assert.All(migrationCommands, command =>
        {
            var arguments = Regex.Matches(command.Value, @"\S+")
                .Select(match => match.Value)
                .ToArray();
            var contextIndex = Assert.Single(arguments
                .Select((argument, index) => (argument, index))
                .Where(item => item.argument == "--context")
                .Select(item => item.index));

            Assert.True(contextIndex + 1 < arguments.Length, "The --context argument must have a value.");
            Assert.Equal("Wayfarer.Models.ApplicationDbContext", arguments[contextIndex + 1]);
        });
    }

    private static string RepositoryFile(params string[] parts) => Path.GetFullPath(
        Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", .. parts]));
}
