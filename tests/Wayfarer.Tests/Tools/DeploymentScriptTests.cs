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

    /// <summary>Native scripts prepare new image storage while retaining the old deployment tree.</summary>
    [Fact]
    public void ImageCacheTransitionRetainsLegacyAndPreparesOwnedCurrentRoot()
    {
        foreach (var name in new[] { "install.sh", "deploy.sh" })
        {
            var script = File.ReadAllText(RepositoryFile("deployment", name));
            Assert.Contains("/var/cache/wayfarer/images", script);
            Assert.Matches(@"(?m)^sudo (?:chown|install)[^\r\n]*APP_USER[^\r\n]*/var/cache/wayfarer/images", script);
        }
        var deploy = File.ReadAllText(RepositoryFile("deployment", "deploy.sh"));
        Assert.Contains("--exclude 'ImageCache'", deploy);
        Assert.Contains("\"$DEPLOY_DIR/ImageCache\"", deploy);
        var smoke = File.ReadAllText(RepositoryFile("tools", "trip-editor-asset-smoke.mjs"));
        Assert.Contains("Storage__CacheRoot: path.join(localDir, 'asset-smoke-cache', 'current')", smoke);
    }

    /// <summary>The bounded prefix bypasses regex image handling and preserves standard proxy headers.</summary>
    [Fact]
    public void ThumbnailsReachKestrelAndNativeRootsArePrepared()
    {
        var nginx = File.ReadAllText(RepositoryFile("deployment", "wayfarer-nginx-vhost.conf"));
        var route = Regex.Match(nginx, @"location \^~ /thumbs/ \{([^}]+)\}");
        Assert.True(route.Success);
        Assert.Contains("proxy_pass http://localhost:5000;", route.Value);
        Assert.DoesNotContain("root ", route.Value);
        foreach (var header in new[] { "Host", "X-Forwarded-For", "X-Forwarded-Proto", "X-Real-IP" })
            Assert.Contains("proxy_set_header " + header, route.Value);
        Assert.Contains("location ~*", nginx);
        foreach (var name in new[] { "install.sh", "deploy.sh" })
        {
            var script = File.ReadAllText(RepositoryFile("deployment", name));
            foreach (var root in new[] { "/var/cache/wayfarer/thumbnails/trips", "/var/log/wayfarer" })
                Assert.Matches(@"(?m)^sudo (?:chown|install)[^\r\n]*APP_USER[^\r\n]*" + root, script);
        }
        var deploy = File.ReadAllText(RepositoryFile("deployment", "deploy.sh"));
        Assert.Contains("--exclude 'wwwroot/thumbs/'", deploy);
        Assert.Contains("--exclude 'Logs'", deploy);
        foreach (var name in new[] { "trip-editor-asset-smoke.mjs", "run-407-waypoint-browser.ps1", "start-shared-layout-e2e-host.ps1" })
        {
            var runner = File.ReadAllText(RepositoryFile("tools", name));
            Assert.Contains("Storage__LogRoot", runner);
            Assert.Contains("Storage__CacheRoot", runner);
            Assert.DoesNotContain("Logging__LogFilePath__Default", runner);
        }
        foreach (var name in new[] { "appsettings.json", "appsettings.Development.json", "appsettings.Production.json" })
            Assert.DoesNotContain("LogFilePath", File.ReadAllText(RepositoryFile(name)));
    }

    /// <summary>Finds source scripts from the test output directory.</summary>
    private static string RepositoryFile(params string[] parts) => Path.GetFullPath(
        Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", .. parts]));
}
