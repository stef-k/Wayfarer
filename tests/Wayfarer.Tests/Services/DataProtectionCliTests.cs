using System.Diagnostics;
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
        profile.StableProtectedCredential = null;
        db.Add(profile);
        await db.SaveChangesAsync();
        Assert.Equal(1, await RunProcessAsync("status", directory.Path, ring, fixture.ConnectionString));
        Assert.Null((await db.PersonalLocationProviderProfiles.AsNoTracking().SingleAsync()).StableProtectedCredential);
        Assert.Equal(0, await RunProcessAsync("prepare-stable-identity", directory.Path, ring, fixture.ConnectionString));
        Assert.Equal(0, await RunProcessAsync("status", directory.Path, ring, fixture.ConnectionString));
        db.ChangeTracker.Clear();
        profile = await db.PersonalLocationProviderProfiles.SingleAsync();
        Assert.True(owner.Read(profile).Succeeded);
        Assert.True(owner.ReadStable(profile).Succeeded);
        profile.StableProtectedCredential = StableIdentityCryptographyTests.Secret;
        await db.SaveChangesAsync();
        Assert.Equal(1, await RunProcessAsync("status", directory.Path, ring, fixture.ConnectionString));
        Assert.Equal(1, await RunProcessAsync("prepare-stable-identity", directory.Path, ring, fixture.ConnectionString));
        Assert.Equal(0, await db.AuditLogs.CountAsync());
        Assert.Equal(1, await db.Users.CountAsync());
    }

    /// <summary>Passes authority configuration only through the environment, captures both streams and enforces termination.</summary>
    private static async Task<int> RunProcessAsync(string command, string root, string ring, string connection)
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
        start.Environment["DataProtection__KeyRingPath"] = ring;
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
