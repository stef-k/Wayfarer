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

        var token = await service.CreateApiTokenAsync(user.Id, "reports");

        Assert.Equal(user.Id, token.UserId);
        Assert.Equal("reports", token.Name);
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
        db.ApiTokens.Add(new ApiToken { Id = 1, UserId = user.Id, User = user, Name = "mobile", Token = "old", CreatedAt = DateTime.UtcNow.AddDays(-1) });
        await db.SaveChangesAsync();
        var service = new ApiTokenService(db, MockUserManager(user).Object);

        var regenerated = await service.RegenerateTokenAsync(user.Id, "mobile");

        Assert.NotEqual("old", regenerated.Token);
        Assert.Equal("mobile", regenerated.Name);
    }

    [Fact]
    public async Task ValidateApiTokenAsync_ReturnsTrueWhenMatch()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u3", username: "carol");
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { Id = 2, UserId = user.Id, User = user, Name = "integration", Token = "tok123" });
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
        db.ApiTokens.Add(new ApiToken { Id = 3, UserId = user.Id, User = user, Name = "old", Token = "tok" });
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
        Assert.Equal("FitBit", stored.Name);
        Assert.Single(db.ApiTokens);
    }

    [Fact]
    public void GenerateToken_ProducesUrlSafePrefixedToken()
    {
        var db = CreateDbContext();
        var service = new ApiTokenService(db, MockUserManager(null).Object);

        var token = service.GenerateToken();

        Assert.StartsWith("wf_", token);
        Assert.True(Regex.IsMatch(token.Substring(3), @"^[A-Za-z0-9\-_]+$"));
    }

    private static Mock<UserManager<ApplicationUser>> MockUserManager(ApplicationUser? user)
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        var mgr = new Mock<UserManager<ApplicationUser>>(store.Object, null, null, null, null, null, null, null, null);
        mgr.Setup(m => m.FindByIdAsync(It.IsAny<string>())).ReturnsAsync(user);
        return mgr;
    }
}
