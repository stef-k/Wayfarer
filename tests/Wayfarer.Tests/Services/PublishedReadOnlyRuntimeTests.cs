using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Opt-in Linux qualification of the actual published host with a read-only application payload.</summary>
[Collection(PlaywrightEnvironmentTestCollection.Name)]
public sealed class PublishedReadOnlyRuntimeTests
{
    /// <summary>Runs the real thumbnail endpoint against a disposable database and external writable roots.</summary>
    [PublishedRuntimeFact]
    [Trait("Category", "RequiresPlaywright")]
    [Trait("Category", "RequiresPublishedRuntime")]
    public async Task PublishedHostGeneratesThumbnailWithoutChangingReadOnlyPayload()
    {
        var source = Environment.GetEnvironmentVariable("WAYFARER_TEST_PUBLISH_DIRECTORY");
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(source))
            throw new InvalidOperationException("Published runtime qualification requires a Linux publish directory.");
        using var directory = new TestDirectory();
        var database = new PostgresMigrationTestFixture();
        await database.InitializeAsync();
        try
        {
            database.RequireAvailable();
            var tripId = await CreatePublicTripAsync(database);
            var app = Path.Combine(directory.Path, "app");
            CopyPublish(source, app);
            var before = Snapshot(app);
            SetWritable(app, false);
            try
            {
                // A privileged process must not silently turn chmod into false read-only evidence.
                Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(app, ".probe"), "probe"));
                await RunPublishedHostAsync(app, directory.Path, database.ConnectionString, tripId);
                Assert.Equal(before, Snapshot(app));
            }
            finally { SetWritable(app, true); }
        }
        finally { await database.DisposeAsync(); }
    }

    /// <summary>Uses the established database owner and creates only a public trip in its disposable database.</summary>
    private static async Task<Guid> CreatePublicTripAsync(PostgresMigrationTestFixture database)
    {
        var user = await database.CreateUserAsync();
        var trip = new Trip
        {
            Id = Guid.NewGuid(), UserId = user.Id, Name = "Read-only runtime qualification",
            IsPublic = true, CenterLat = 37.98, CenterLon = 23.73, Zoom = 2, UpdatedAt = DateTime.UtcNow
        };
        await using var context = database.CreateContext();
        context.Trips.Add(trip);
        await context.SaveChangesAsync();
        return trip.Id;
    }

    /// <summary>Exercises HTTP/static serving and the production loopback browser path; always stops the owned host.</summary>
    private static async Task RunPublishedHostAsync(string app, string root, string connection, Guid tripId)
    {
        var port = FreePort();
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = app, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(app, "Wayfarer.dll"));
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production", ["DOTNET_ENVIRONMENT"] = "Production",
            ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
            ["Kestrel__Endpoints__Http__Url"] = $"http://127.0.0.1:{port}",
            ["AllowedHosts"] = "wayfarer.example.com",
            ["ConnectionStrings__DefaultConnection"] = connection,
            ["Storage__DataRoot"] = Path.Combine(root, "data"),
            ["Storage__CacheRoot"] = Path.Combine(root, "cache"),
            ["Storage__LogRoot"] = Path.Combine(root, "logs"),
            ["Storage__TempRoot"] = Path.Combine(root, "temp"),
            ["DataProtection__KeyRingPath"] = Path.Combine(root, "ring"),
            ["TMPDIR"] = Path.Combine(root, "temp")
        }) start.Environment[key] = value;
        Directory.CreateDirectory(Path.Combine(root, "temp"));
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.Host = "wayfarer.example.com";
            await WaitForHostAsync(http, process);
            using var asset = await http.GetAsync("/lib/leaflet/leaflet-1.9.4.js");
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
            using var snapshot = await http.GetAsync($"/Public/Trips/{tripId}/MapSnapshot");
            Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
            var bytes = await snapshot.Content.ReadAsByteArrayAsync();
            Assert.True(bytes.Length > 1000);
            Assert.Equal(new byte[] { 0xff, 0xd8 }, bytes.Take(2));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, "cache", "thumbnails", "trips"), "*.jpg"));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, "ring"), "key-*.xml"));
        }
        finally
        {
            if (!process.HasExited)
            {
                using var signal = Process.Start("kill", $"-INT {process.Id}")!;
                await signal.WaitForExitAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); }
            }
            var output = await stdout + await stderr;
            // Retain failure diagnostics without writing into the immutable application tree.
            var evidence = Environment.GetEnvironmentVariable("WAYFARER_TEST_ARTIFACT_DIRECTORY");
            if (!string.IsNullOrEmpty(evidence))
            {
                Directory.CreateDirectory(evidence);
                await File.WriteAllTextAsync(Path.Combine(evidence, "read-only-host.log"), output);
            }
        }
        Assert.Equal(0, process.ExitCode);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, "logs"), "*", SearchOption.AllDirectories));
    }

    /// <summary>Bounds readiness without requiring Chromium during startup.</summary>
    private static async Task WaitForHostAsync(HttpClient http, Process process)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            Assert.False(process.HasExited, "Published host exited before readiness; inspect read-only-host.log.");
            try
            {
                using var response = await http.GetAsync("/Identity/Account/Login");
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(250);
        }
        throw new TimeoutException("Published host did not become ready.");
    }

    /// <summary>Allocates a loopback port for the owned host.</summary>
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Copies release files and executable permissions into the fixture-owned tree.</summary>
    [SupportedOSPlatform("linux")]
    private static void CopyPublish(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var path in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, path)));
        foreach (var path in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(target, Path.GetRelativePath(source, path));
            File.Copy(path, copy);
            File.SetUnixFileMode(copy, File.GetUnixFileMode(path));
        }
    }

    /// <summary>Removes all write bits; restores owner writes only for fixture cleanup.</summary>
    [SupportedOSPlatform("linux")]
    private static void SetWritable(string root, bool writable)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root))
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, writable ? mode | UnixFileMode.UserWrite
                : mode & ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
        }
    }

    /// <summary>Records every relative entry and file digest to detect new directories and changed bytes.</summary>
    private static string[] Snapshot(string root) => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path) + (File.Exists(path)
            ? ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "/"))
        .Order(StringComparer.Ordinal).ToArray();
}

/// <summary>Marks unavailable qualification prerequisites as discovery-time skips for the xUnit v2 runner.</summary>
public sealed class PublishedRuntimeFactAttribute : FactAttribute
{
    /// <summary>Requires Linux, an explicit publish and the established guarded PostgreSQL connection.</summary>
    public PublishedRuntimeFactAttribute()
    {
        if (!OperatingSystem.IsLinux()
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYFARER_TEST_PUBLISH_DIRECTORY"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYFARER_TEST_POSTGRES_CONNECTION")))
            Skip = "Requires Linux, WAYFARER_TEST_PUBLISH_DIRECTORY and WAYFARER_TEST_POSTGRES_CONNECTION.";
    }
}
