using System.Diagnostics;
using Npgsql;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.CommandLine;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Tests the actual executable command path and its bounded argument/output contract.</summary>
[Collection(PostgresEnvironmentEvidenceTestCollection.Name)]
public sealed class DataProtectionCliTests
{
    /// <summary>Invalid arguments are rejected before configuration/database access and never echoed.</summary>
    [Theory]
    [InlineData("unknown")]
    [InlineData("status")]
    public async Task Arguments_DoNotAcceptOrEchoSecrets(string command)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await DataProtectionCli.RunAsync(
            ["data-protection", command, StableIdentityCryptographyTests.Secret], output, error));
        Assert.Empty(output.ToString());
        Assert.False(error.ToString().Contains(StableIdentityCryptographyTests.Secret, StringComparison.Ordinal));
    }

    /// <summary>The executable prepares the configured source database without normal host startup or audit writes.</summary>
    [PostgresFact]
    public async Task Process_StatusPrepareStatusAndCorruption_ReturnExpectedExitCodes()
    {
        await using var fixture = new PostgresMigrationTestFixture();
        await fixture.InitializeAsync();
        using var directory = new TestDirectory();
        var ring = Path.Combine(directory.Path, "ring");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "appsettings.json"), "{}");
        await using var host = StableIdentityCryptographyTests.Host(directory.Path, ring);
        var owner = host.Services.GetRequiredService<PersonalProviderCredentialService>();
        var user = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var profile = PersonalLocationProviderProfile.Create(user.Id, PersonalLocationProvider.Geoapify);
        owner.Replace(profile, StableIdentityCryptographyTests.Secret);
        var legacy = DataProtectionProvider.Create(new DirectoryInfo(ring), options =>
            options.SetApplicationName(directory.Path + Path.DirectorySeparatorChar).DisableAutomaticKeyGeneration());
        profile.ProtectedCredential = PersonalProviderCredentialService.Protector(profile, legacy)
            .Protect(StableIdentityCryptographyTests.Secret);
        profile.StableProtectedCredential = null;
        db.Add(profile);
        await db.SaveChangesAsync();
        var keysBefore = Directory.GetFiles(ring).ToDictionary(file => Path.GetFileName(file)!, File.ReadAllBytes);
        Assert.Equal(1, await RunProcessAsync("status", directory.Path, ring, fixture.ConnectionString));
        Assert.Null((await db.PersonalLocationProviderProfiles.AsNoTracking().SingleAsync()).StableProtectedCredential);
        Assert.Equal(0, await RunProcessAsync("prepare-stable-identity", directory.Path, ring, fixture.ConnectionString));
        Assert.Equal(0, await RunProcessAsync("status", directory.Path, ring, fixture.ConnectionString));
        db.ChangeTracker.Clear();
        profile = await db.PersonalLocationProviderProfiles.SingleAsync();
        Assert.True(owner.Read(profile).Succeeded);
        var target = Directory.CreateDirectory(Path.Combine(directory.Path, "target")).FullName;
        var copiedRing = Directory.CreateDirectory(Path.Combine(target, "data-protection")).FullName;
        await File.WriteAllTextAsync(Path.Combine(target, "appsettings.json"), "{}");
        foreach (var file in Directory.GetFiles(ring)) File.Copy(file, Path.Combine(copiedRing, Path.GetFileName(file)));
        await using var targetFixture = new PostgresMigrationTestFixture();
        await targetFixture.InitializeAsync();
        await CopyDatabaseAsync(fixture, targetFixture, directory.Path);
        Assert.Equal(0, await RunProcessAsync("status", target, copiedRing, targetFixture.ConnectionString, useStorage: true));
        Assert.Equal(1, await RunProcessAsync("prepare-stable-identity", target, copiedRing, targetFixture.ConnectionString, useStorage: true));
        Assert.Equal(0, await RunProcessAsync("status", target, copiedRing, targetFixture.ConnectionString, useStorage: true));
        foreach (var path in new[] { ring, copiedRing })
        {
            Assert.Equal(keysBefore.Count, Directory.GetFiles(path).Length);
            foreach (var file in Directory.GetFiles(path))
                Assert.True(keysBefore[Path.GetFileName(file)].SequenceEqual(File.ReadAllBytes(file)));
        }
        profile.StableProtectedCredential = StableIdentityCryptographyTests.Secret;
        await db.SaveChangesAsync();
        Assert.Equal(1, await RunProcessAsync("status", directory.Path, ring, fixture.ConnectionString));
        Assert.Equal(1, await RunProcessAsync("prepare-stable-identity", directory.Path, ring, fixture.ConnectionString));
        Assert.Equal(0, await db.AuditLogs.CountAsync());
        Assert.Equal(1, await db.Users.CountAsync());
    }

    /// <summary>Copies a full disposable database recovery set using PostgreSQL's native dump/restore tools.</summary>
    private static async Task CopyDatabaseAsync(PostgresMigrationTestFixture source, PostgresMigrationTestFixture target, string root)
    {
        var dump = Path.Combine(root, "fixture.dump");
        await RunDatabaseToolAsync("pg_dump", source.ConnectionString, ["--format=custom", "--file", dump]);
        await RunDatabaseToolAsync("pg_restore", target.ConnectionString, ["--clean", "--if-exists", "--no-owner", "--exit-on-error", dump]);
    }

    /// <summary>Supplies private connection configuration through the environment and never emits tool diagnostics.</summary>
    private static async Task RunDatabaseToolAsync(string tool, string connection, string[] arguments)
    {
        var settings = new NpgsqlConnectionStringBuilder(connection);
        Assert.StartsWith(PostgresMigrationTestFixture.DatabasePrefix, settings.Database);
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.Environment["PGHOST"] = settings.Host;
        start.Environment["PGPORT"] = settings.Port.ToString();
        start.Environment["PGUSER"] = settings.Username;
        start.Environment["PGPASSWORD"] = settings.Password;
        start.Environment["PGDATABASE"] = settings.Database;
        if (tool == "pg_restore") { start.ArgumentList.Add("--dbname"); start.ArgumentList.Add(settings.Database!); }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        await Task.WhenAll(output, error);
        Assert.True(process.ExitCode == 0, "Disposable database copy failed.");
    }

    /// <summary>Passes authority configuration only through the environment, captures both streams and enforces termination.</summary>
    private static async Task<int> RunProcessAsync(string command, string root, string ring, string connection, bool useStorage = false)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(DataProtectionCli).Assembly.Location);
        start.ArgumentList.Add("data-protection");
        start.ArgumentList.Add(command);
        start.Environment["ConnectionStrings__DefaultConnection"] = connection;
        start.Environment["DataProtection__KeyRingPath"] = useStorage ? "" : ring;
        // Isolate the historical per-user default as well as the current Storage path.
        start.Environment["XDG_DATA_HOME"] = Path.Combine(root, "xdg-data");
        start.Environment["Storage__DataRoot"] = root;
        start.Environment["Storage__CacheRoot"] = Path.Combine(root, "cache");
        start.Environment["Storage__LogRoot"] = Path.Combine(root, "logs");
        start.Environment["Storage__TempRoot"] = Path.Combine(root, "temp");
        start.Environment["ASPNETCORE_CONTENTROOT"] = root;
        start.Environment["DOTNET_CONTENTROOT"] = root;
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        using var process = Process.Start(start)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var text = await output + await error;
            Assert.False(text.Contains(StableIdentityCryptographyTests.Secret, StringComparison.Ordinal));
            Assert.False(text.Contains("migration-fixture-", StringComparison.Ordinal));
            Assert.False(text.Contains("CfDJ", StringComparison.Ordinal));
            Assert.False(text.Contains("<key", StringComparison.Ordinal));
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }
}
