using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Moq;
using Wayfarer.Models;
using Wayfarer.Util;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// API token service behaviors (generate, store, validate).
/// </summary>
public class ApiTokenServiceTests : TestBase
{
    [Fact]
    public async Task CreateApiTokenAsync_PersistsTokenForUser()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var manager = MockUserManager(user);
        var service = new ApiTokenService(db, manager.Object);

        var (apiToken, plainToken) = await service.CreateApiTokenAsync(user.Id, "reports");

        Assert.Equal(user.Id, apiToken.UserId);
        Assert.Equal("reports", apiToken.Name);
        Assert.NotEmpty(plainToken); // Plain token is returned for one-time display
        Assert.Null(apiToken.Token); // Plain token is NOT stored in DB
        Assert.NotNull(apiToken.TokenHash); // Hash is stored instead
        Assert.Single(db.ApiTokens);
        manager.Verify(m => m.FindByIdAsync(user.Id), Times.Once);
    }

    [Fact]
    public async Task CreateApiTokenAsync_Throws_WhenUserMissing()
    {
        var db = CreateDbContext();
        var manager = MockUserManager(null);
        var service = new ApiTokenService(db, manager.Object);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateApiTokenAsync("missing", "x"));
    }

    [Fact]
    public async Task RegenerateTokenAsync_ReplacesTokenString()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u2", username: "bob");
        db.Users.Add(user);
        var oldHash = ApiTokenService.HashToken("old");
        db.ApiTokens.Add(new ApiToken { Id = 1, UserId = user.Id, User = user, Name = "mobile", Token = null, TokenHash = oldHash, CreatedAt = DateTime.UtcNow.AddDays(-1) });
        await db.SaveChangesAsync();
        var service = new ApiTokenService(db, MockUserManager(user).Object);

        var (apiToken, plainToken) = await service.RegenerateTokenAsync(user.Id, "mobile");

        Assert.NotEmpty(plainToken); // New plain token returned for one-time display
        Assert.NotEqual(oldHash, apiToken.TokenHash); // Hash changed
        Assert.Equal("mobile", apiToken.Name);
        Assert.Null(apiToken.Token); // Plain token not stored
    }

    [Fact]
    public async Task ValidateApiTokenAsync_ReturnsTrueWhenMatch()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u3", username: "carol");
        db.Users.Add(user);
        // Test with hashed token (Wayfarer-generated)
        var tokenHash = ApiTokenService.HashToken("tok123");
        db.ApiTokens.Add(new ApiToken { Id = 2, UserId = user.Id, User = user, Name = "integration", Token = null, TokenHash = tokenHash });
        await db.SaveChangesAsync();
        var service = new ApiTokenService(db, MockUserManager(user).Object);

        var valid = await service.ValidateApiTokenAsync(user.Id, "tok123");
        var invalid = await service.ValidateApiTokenAsync(user.Id, "wrong");

        Assert.True(valid);
        Assert.False(invalid);
    }

    [Fact]
    public async Task DeleteTokenForUserAsync_RemovesToken()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u4", username: "dave");
        db.Users.Add(user);
        var tokenHash = ApiTokenService.HashToken("tok");
        db.ApiTokens.Add(new ApiToken { Id = 3, UserId = user.Id, User = user, Name = "old", Token = null, TokenHash = tokenHash });
        await db.SaveChangesAsync();
        var service = new ApiTokenService(db, MockUserManager(user).Object);

        await service.DeleteTokenForUserAsync(user.Id, 3);

        Assert.Empty(db.ApiTokens);
    }

    [Fact]
    public async Task StoreThirdPartyToken_SavesProvidedToken()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u5", username: "erin");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var service = new ApiTokenService(db, MockUserManager(user).Object);

        var stored = await service.StoreThirdPartyToken(user.Id, "FitBit", "third-party-token");

        Assert.Equal("third-party-token", stored.Token);
        Assert.Null(stored.TokenHash); // Third-party tokens don't use hashing
        Assert.Equal("FitBit", stored.Name);
        Assert.Single(db.ApiTokens);
    }

    [Fact]
    public async Task ValidateApiTokenAsync_WorksWithThirdPartyPlainTokens()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u6", username: "frank");
        db.Users.Add(user);
        // Third-party token stored in plain text
        db.ApiTokens.Add(new ApiToken { Id = 4, UserId = user.Id, User = user, Name = "Mapbox", Token = "mapbox-token-123", TokenHash = null });
        await db.SaveChangesAsync();
        var service = new ApiTokenService(db, MockUserManager(user).Object);

        var valid = await service.ValidateApiTokenAsync(user.Id, "mapbox-token-123");
        var invalid = await service.ValidateApiTokenAsync(user.Id, "wrong");

        Assert.True(valid);
        Assert.False(invalid);
    }

    [Fact]
    public void GenerateToken_ProducesUrlSafePrefixedToken()
    {
        var db = CreateDbContext();
        var service = new ApiTokenService(db, MockUserManager(null).Object);

        var token = service.GenerateToken();

        Assert.StartsWith("wf_", token);
        Assert.Matches(@"^[A-Za-z0-9\-_]+$", token.Substring(3));
    }

    [Fact]
    public void HashToken_ProducesConsistentSha256Hash()
    {
        // Same input should always produce same hash
        var hash1 = ApiTokenService.HashToken("test-token");
        var hash2 = ApiTokenService.HashToken("test-token");

        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length); // SHA-256 = 64 hex chars
        Assert.Matches(@"^[a-f0-9]+$", hash1); // Lowercase hex

        // Different input should produce different hash
        var hash3 = ApiTokenService.HashToken("different-token");
        Assert.NotEqual(hash1, hash3);
    }

    [Fact]
    public async Task CreateAndValidate_RoundTrip_WorksWithReturnedPlainToken()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u7", username: "grace");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var service = new ApiTokenService(db, MockUserManager(user).Object);

        // Create token - get back plain token for one-time display
        var (apiToken, plainToken) = await service.CreateApiTokenAsync(user.Id, "mobile-app");

        // The returned plain token should validate successfully
        var isValid = await service.ValidateApiTokenAsync(user.Id, plainToken);

        Assert.True(isValid);
        // Verify hash matches
        Assert.Equal(ApiTokenService.HashToken(plainToken), apiToken.TokenHash);
    }

    [Fact]
    public async Task RegenerateTokenAsync_Throws_WhenTokenNotFound()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u8", username: "henry");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var service = new ApiTokenService(db, MockUserManager(user).Object);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RegenerateTokenAsync(user.Id, "nonexistent-token"));
    }

    private static Mock<UserManager<ApplicationUser>> MockUserManager(ApplicationUser? user)
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        var mgr = new Mock<UserManager<ApplicationUser>>(store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        mgr.Setup(m => m.FindByIdAsync(It.IsAny<string>())).ReturnsAsync(user);
        return mgr;
    }
}
