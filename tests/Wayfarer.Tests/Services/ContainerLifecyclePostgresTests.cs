using System.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Exercises the shipped maintenance executable and nonmutating readiness on an owned database.</summary>
public sealed class ContainerLifecyclePostgresTests : IClassFixture<PostgresMigrationTestFixture>
{
    private readonly PostgresMigrationTestFixture fixture;

    /// <summary>Uses the established disposable database ownership boundary.</summary>
    public ContainerLifecyclePostgresTests(PostgresMigrationTestFixture fixture) => this.fixture = fixture;

    /// <summary>Migration, seed and protected bootstrap are separate; readiness rejects drift without repair.</summary>
    [PostgresFact]
    public async Task ExplicitMaintenance_PreparesState_WithoutStartingWebOrJobs()
    {
        fixture.RequireAvailable();
        var root = Path.Combine(Path.GetTempPath(), "wayfarer-640-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var db = fixture.CreateContext();
            // This fixture owns the entire database. Reset only that owned schema for a fresh migration proof.
            await db.Database.ExecuteSqlRawAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public; CREATE EXTENSION postgis; CREATE EXTENSION citext;");
            var unprepared = await RunAsync(root, null);
            Assert.NotEqual(0, unprepared.Code);
            Assert.Contains("Application is not prepared", unprepared.Output);
            Assert.Equal(0, (await RunAsync(root, null, "database", "migrate")).Code);
            Assert.False(await ReadyAsync());
            Assert.Equal(0, (await RunAsync(root, null, "database", "seed")).Code);
            Assert.Equal(0, (await RunAsync(root, null, "database", "seed")).Code);
            Assert.NotEqual(0, (await RunAsync(root, null)).Code);
            Assert.False(await db.Users.AnyAsync());
            Assert.False(await ReadyAsync());
            var password = "Protected-" + Guid.NewGuid().ToString("N") + "!7";
            var bootstrap = await RunAsync(root, password, "admin", "bootstrap", "operator", "--stdin");
            Assert.Equal(0, bootstrap.Code);
            Assert.DoesNotContain(password, bootstrap.Output);
            Assert.True(await ReadyAsync());
            Assert.Equal(1, (await RunAsync(root, "Admin1!", "admin", "reset", "operator", "--stdin")).Code);
            Assert.Equal(0, (await RunAsync(root, password + "new", "admin", "reset", "operator", "--stdin")).Code);
            var lookup = await RunAsync(root, null, "user", "find", "operator");
            Assert.Equal(0, lookup.Code);
            Assert.Contains("operator", lookup.Output);
            Assert.DoesNotContain("PasswordHash", lookup.Output);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE qrtz_triggers DROP COLUMN preferred_node;");
            Assert.False(await ReadyAsync());
            // The failed probe must leave the incompatible schema untouched.
            Assert.False(await ReadyAsync());
            Assert.Equal(0, (await RunAsync(root, null, "database", "migrate")).Code);
            Assert.True(await ReadyAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Builds only the dependencies needed by the production readiness checker.</summary>
    private async Task<bool> ReadyAsync()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(fixture.ConnectionString,
            provider => provider.UseNetTopologySuite()));
        services.AddScoped<IPasswordHasher<ApplicationUser>, PasswordHasher<ApplicationUser>>();
        await using var provider = services.BuildServiceProvider();
        return await ApplicationReadiness.IsReadyAsync(provider);
    }

    /// <summary>Starts the built application with private process configuration and protected stdin.</summary>
    private async Task<(int Code, string Output)> RunAsync(string root, string? password, params string[] args)
    {
        var assembly = typeof(ApplicationDbContext).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(assembly);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["ConnectionStrings__DefaultConnection"] = fixture.ConnectionString;
        start.Environment["DataProtection__KeyRingPath"] = Path.Combine(root, "keys");
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        foreach (var authority in new[] { "Data", "Cache", "Log", "Temp" })
            start.Environment[$"Storage__{authority}Root"] = Path.Combine(root, authority);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (password is not null) await process.StandardInput.WriteLineAsync(password);
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); throw; }
        return (process.ExitCode, await output + await error);
    }
}
