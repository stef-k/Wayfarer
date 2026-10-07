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
