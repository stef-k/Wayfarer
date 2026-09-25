using System.Text.RegularExpressions;
using System.Diagnostics;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Tools;

/// <summary>Guards repository scaffolding and supported native deployment contracts.</summary>
public sealed class DeploymentScriptTests
{
    /// <summary>Current source needs no runtime scaffolding while native legacy exclusions remain protected.</summary>
    [Fact]
    public void RuntimeScaffoldingIsAbsentAndCurrentImportDocsUseExternalStorage()
    {
        var ignore = File.ReadAllText(RepositoryFile(".gitignore"));
        foreach (var legacy in new[] { "Uploads", "TileCache", "ImageCache" })
        {
            Assert.False(File.Exists(RepositoryFile(legacy, ".gitkeep")));
            Assert.Contains(legacy + "/*", ignore);
            Assert.DoesNotContain("!" + legacy + "/.gitkeep", ignore);
        }
        Assert.DoesNotContain("!Uploads/Temp/.gitkeep", ignore);
        var project = System.Xml.Linq.XDocument.Load(RepositoryFile("Wayfarer.csproj"));
        Assert.DoesNotContain(project.Descendants("Folder"), folder =>
            ((string?)folder.Attribute("Include"))?.Replace('\\', '/').StartsWith("Uploads/") == true);
        foreach (var name in new[] { "16-Configuration.md", "17-Services.md" })
        {
            var document = File.ReadAllText(RepositoryFile("docs", name));
            Assert.Contains("Storage:DataRoot/uploads/imports", document);
            Assert.DoesNotContain("Upload staging directory defaults under `Uploads/Temp/`", document);
            Assert.DoesNotContain("Files uploaded to `Uploads/Temp/`", document);
        }
    }

    /// <summary>All current browser consumers delegate launch without an installer or discovery mutation.</summary>
    [Fact]
    public void BrowserConsumersUsePreinstalledRuntimeOnly()
    {
        foreach (var name in new[] { "MapSnapshotService", "TripMapThumbnailGenerator", "TripExportService" })
        {
            var source = File.ReadAllText(RepositoryFile("Services", name + ".cs"));
            Assert.Contains("BrowserRuntime.LaunchAsync(", source);
            Assert.DoesNotContain(".Chromium.LaunchAsync(", source);
            Assert.DoesNotContain("Program.Main", source);
            Assert.DoesNotContain("SetEnvironmentVariable", source);
            Assert.DoesNotContain("ChromeCache", source);
        }
        var policy = File.ReadAllText(RepositoryFile("Services", "BrowserRuntime.cs"));
        Assert.DoesNotContain("Program.Main", policy);
        Assert.DoesNotContain("Process.Start", policy);
        foreach (var name in new[] { "appsettings.json", "appsettings.Production.json" })
            Assert.DoesNotContain("ChromeCache", File.ReadAllText(RepositoryFile(name)));
    }

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
        Assert.DoesNotContain("--exclude 'wwwroot/thumbs/'", deploy);
        Assert.DoesNotContain("--exclude 'Logs'", deploy);
        Assert.DoesNotContain("ChromeCache", deploy);
        foreach (var legacy in new[] { "Uploads", "TileCache", "ImageCache" })
            Assert.Contains("--exclude '" + legacy + "'", deploy);
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

    /// <summary>Actual template refresh retains quoted compatibility paths and gives new installs no old override.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Environment=DataProtection__KeyRingPath=/home/wayfarer/.aspnet/DataProtection-Keys")]
    [InlineData("Environment=\"DataProtection__KeyRingPath=/srv/retained ring\"")]
    [InlineData("Environment=\"Other=retained\" \"DataProtection__KeyRingPath=/srv/retained ring\"")]
    public async Task ServiceRefresh_PreservesExactInstalledKeyRingAssignment(string? assignment)
    {
        using var directory = new TestDirectory();
        var installed = Path.Combine(directory.Path, "wayfarer.service");
        if (assignment != null) await File.WriteAllTextAsync(installed, "[Service]\n" + assignment + "\n[Install]\nWantedBy=multi-user.target\n");
        var start = new ProcessStartInfo("python3") { RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(RepositoryFile("deployment", "refresh-service.py"));
        start.ArgumentList.Add(RepositoryFile("deployment", "wayfarer.service"));
        start.ArgumentList.Add(installed);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        var text = await File.ReadAllTextAsync(installed);
        var active = text.Split('\n').Where(line => !line.TrimStart().StartsWith('#') && line.Contains("DataProtection__KeyRingPath")).ToArray();
        if (assignment == null) Assert.Empty(active);
        else Assert.Equal(assignment, Assert.Single(active));
        foreach (var name in new[] { "install.sh", "deploy.sh" })
        {
            var script = File.ReadAllText(RepositoryFile("deployment", name));
            Assert.Contains("install -d -m 700 -o \"$APP_USER\" -g \"$APP_USER\" /var/lib/wayfarer/data-protection", script);
            Assert.DoesNotContain("sudo mkdir -p \"/home/$APP_USER/.aspnet/DataProtection-Keys\"", script);
        }
        Assert.Contains("refresh-service.py", File.ReadAllText(RepositoryFile("deployment", "install.sh")));
    }

    /// <summary>Finds source scripts from the test output directory.</summary>
    private static string RepositoryFile(params string[] parts) => Path.GetFullPath(
        Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", .. parts]));
}
