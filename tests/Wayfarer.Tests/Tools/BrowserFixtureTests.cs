using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Tools;

/// <summary>Proves actual browser fixture database authority and unrelated credential preservation.</summary>
[Collection(PostgresPrerequisiteTestCollection.Name)]
public sealed class BrowserFixtureTests : TestBase
{
    /// <summary>Rejects development databases and custom schema redirection before connecting.</summary>
    [Theory]
    [InlineData("Host=guarded;Database=wayfarer")]
    [InlineData("Host=guarded;Database=Wayfarer_import_tests")]
    [InlineData("Host=guarded;Database=wayfarer_import_tests;Search Path=foreign")]
    public void Configuration_RefusesNonGuardedAuthority(string connection) =>
        Assert.Throws<InvalidOperationException>(() => BrowserFixtureGuard.ValidateConfiguration(connection));

    /// <summary>Proves the effective app cannot silently use a different endpoint or credential.</summary>
    [Theory]
    [InlineData("Host=other;Port=5432;Database=wayfarer_import_tests;Username=test")]
    [InlineData("Host=guarded;Port=5433;Database=wayfarer_import_tests;Username=test")]
    [InlineData("Host=guarded;Port=5432;Database=wayfarer;Username=test")]
    [InlineData("Host=guarded;Port=5432;Database=wayfarer_import_tests;Username=human")]
    public void Configuration_RefusesDifferentEffectiveConnection(string effective) =>
        Assert.Throws<InvalidOperationException>(() => BrowserFixtureGuard.ValidateConfiguration(
            "Host=guarded;Port=5432;Database=wayfarer_import_tests;Username=test", effective));

    /// <summary>Equivalent canonical app and fixture connection strings share the same authority.</summary>
    [Fact]
    public void Configuration_AcceptsEquivalentEffectiveConnection() =>
        BrowserFixtureGuard.ValidateConfiguration("Host=guarded;Database=wayfarer_import_tests;Username=test",
            "Username=test;Database=wayfarer_import_tests;Host=guarded");

    /// <summary>A rolled-back preparation record cannot delete a fixed-ID settings row created by another run.</summary>
    [PostgresTheory]
    [InlineData(false, null)]
    [InlineData(true, -1L)]
    public async Task PreparationCleanup_PreservesSettingsWithoutCommittedOwnership(bool committed, long? version)
    {
        await using var fixture = new PostgresImportTestFixture();
        await fixture.InitializeAsync();
        await using var db = fixture.CreateContext();
        var settings = await db.ApplicationSettings.SingleOrDefaultAsync(row => row.Id == 1);
        var ownsSettings = settings == null;
        settings ??= new ApplicationSettings { Id = 1, LocationTimeThresholdMinutes = 90 };
        if (ownsSettings) { db.ApplicationSettings.Add(settings); await db.SaveChangesAsync(); }
        var originalThreshold = settings.LocationTimeThresholdMinutes;
        var path = Path.Combine(CreateTestDirectory(), "partial-preparation.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            Run = Guid.NewGuid().ToString(), AdminId = (string?)null, RoleIds = Array.Empty<string>(),
            SettingsId = 1, Committed = committed, SettingsVersion = version
        }));
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                BrowserFixtureEnvironment.TryRunAsync("cleanup-environment", path, db));
            Assert.Contains(committed ? "left captured identities" : "commit ownership is unproved", failure.Message);
            await using var fresh = fixture.CreateContext();
            Assert.Equal(originalThreshold, (await fresh.ApplicationSettings.SingleAsync(row => row.Id == 1)).LocationTimeThresholdMinutes);
        }
        finally
        {
            if (ownsSettings) await db.ApplicationSettings.Where(row => row.Id == 1).ExecuteDeleteAsync();
        }
    }

    /// <summary>Ordinary preparation records its committed tuple identity and cleans only its created prerequisites.</summary>
    [PostgresFact]
    public async Task PreparationCleanup_RemovesCommittedRunOwnedPrerequisites()
    {
        await using var fixture = new PostgresImportTestFixture();
        await fixture.InitializeAsync();
        await using var db = fixture.CreateContext();
        var originalRun = Environment.GetEnvironmentVariable("WAYFARER_E2E_RUN_ID");
        var originalPassword = Environment.GetEnvironmentVariable("WAYFARER_E2E_PASSWORD");
        var path = Path.Combine(CreateTestDirectory(), "preparation.json");
        try
        {
            Environment.SetEnvironmentVariable("WAYFARER_E2E_RUN_ID", Guid.NewGuid().ToString());
            Environment.SetEnvironmentVariable("WAYFARER_E2E_PASSWORD", "Browser1!Run-owned-prerequisite");
            Assert.True(await BrowserFixtureEnvironment.TryRunAsync("prepare", path, db));
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.True(manifest.RootElement.GetProperty("Committed").GetBoolean());
            if (manifest.RootElement.GetProperty("SettingsId").ValueKind != JsonValueKind.Null)
                Assert.True(manifest.RootElement.GetProperty("SettingsVersion").GetInt64() > 0);
            Assert.True(await BrowserFixtureEnvironment.TryRunAsync("cleanup-environment", path, db));
            Assert.True(await BrowserFixtureEnvironment.TryRunAsync("verify-environment", path, db));
        }
        finally
        {
            if (File.Exists(path)) await BrowserFixtureEnvironment.TryRunAsync("cleanup-environment", path, db);
            Environment.SetEnvironmentVariable("WAYFARER_E2E_RUN_ID", originalRun);
            Environment.SetEnvironmentVariable("WAYFARER_E2E_PASSWORD", originalPassword);
        }
    }

    /// <summary>Replacing the seeded run token and cleaning fixtures leaves another user's token/password intact.</summary>
    [PostgresFact]
    public async Task SharedLayout_ReplacementAndCleanupPreserveUnrelatedIdentity()
    {
        await using var fixture = new PostgresImportTestFixture();
        await fixture.InitializeAsync();
        var other = await fixture.CreateUserAsync();
        var run = Guid.NewGuid().ToString();
        var path = Path.Combine(CreateTestDirectory(), "fixture.json");
        var originalRun = Environment.GetEnvironmentVariable("WAYFARER_E2E_RUN_ID");
        var originalPassword = Environment.GetEnvironmentVariable("WAYFARER_E2E_PASSWORD");
        await using var db = fixture.CreateContext();
        other = await db.Users.SingleAsync(user => user.Id == other.Id);
        other.PasswordHash = "unrelated-password-verifier";
        await db.SaveChangesAsync();
        var service = new ApiTokenService(db, null!);
        var foreign = (await service.CreateConnectionTokenAsync(other.Id))!;
        var role = await db.Roles.SingleOrDefaultAsync(role => role.NormalizedName == "USER");
        var ownsRole = role == null;
        role ??= new IdentityRole("User") { NormalizedName = "USER" };
        if (ownsRole) { db.Roles.Add(role); await db.SaveChangesAsync(); }
        try
        {
            Environment.SetEnvironmentVariable("WAYFARER_E2E_RUN_ID", run);
            Environment.SetEnvironmentVariable("WAYFARER_E2E_PASSWORD", "Browser1!Run-owned-test-password");
            Assert.True(await SharedLayoutFixture.TryRunAsync("provision-layout", path, db));
            var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var userId = manifest.RootElement.GetProperty("userId").GetString()!;
            var owned = (await service.GetConnectionTokenStatusAsync(userId))!;
            Assert.NotNull(await service.ReplaceConnectionTokenAsync(userId, owned.TokenId, owned.IssuedAt));
            Assert.True(await SharedLayoutFixture.TryRunAsync("cleanup-layout", path, db));
            Assert.True(await SharedLayoutFixture.TryRunAsync("verify-cleanup-layout", path, db));
            Assert.True(await SharedLayoutFixture.TryRunAsync("cleanup-layout", path, db));
            await using var fresh = fixture.CreateContext();
            var retained = await fresh.ApiTokens.SingleAsync(token => token.Id == foreign.TokenId);
            Assert.Equal(ApiTokenService.HashToken(foreign.Token), retained.TokenHash);
            Assert.Equal("unrelated-password-verifier", (await fresh.Users.SingleAsync(user => user.Id == other.Id)).PasswordHash);
            Assert.False(await fresh.Users.AnyAsync(user => user.Id == userId));
        }
        finally
        {
            if (File.Exists(path)) await SharedLayoutFixture.TryRunAsync("cleanup-layout", path, db);
            if (ownsRole) await db.Roles.Where(item => item.Id == role.Id).ExecuteDeleteAsync();
            Environment.SetEnvironmentVariable("WAYFARER_E2E_RUN_ID", originalRun);
            Environment.SetEnvironmentVariable("WAYFARER_E2E_PASSWORD", originalPassword);
        }
    }
}
