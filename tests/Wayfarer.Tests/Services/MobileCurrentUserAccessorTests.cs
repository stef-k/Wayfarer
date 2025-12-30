using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests for <see cref="MobileCurrentUserAccessor"/> covering bearer token authentication
/// for mobile API access.
/// </summary>
public class MobileCurrentUserAccessorTests : TestBase
{
    #region GetCurrentUserAsync Tests

    [Fact]
    public async Task GetCurrentUserAsync_ReturnsNull_WhenHttpContextIsNull()
    {
        // Arrange
        var db = CreateDbContext();
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns((HttpContext?)null);

        var accessor = new MobileCurrentUserAccessor(mockHttpContextAccessor.Object, db, NullLogger<MobileCurrentUserAccessor>.Instance);

        // Act
        var result = await accessor.GetCurrentUserAsync();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCurrentUserAsync_ReturnsNull_WhenNoAuthorizationHeader()
    {
        // Arrange
        var db = CreateDbContext();
        var httpContext = new DefaultHttpContext();
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        var accessor = new MobileCurrentUserAccessor(mockHttpContextAccessor.Object, db, NullLogger<MobileCurrentUserAccessor>.Instance);

        // Act
        var result = await accessor.GetCurrentUserAsync();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCurrentUserAsync_ReturnsNull_WhenAuthorizationHeaderIsEmpty()
    {
        // Arrange
        var db = CreateDbContext();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "";
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        var accessor = new MobileCurrentUserAccessor(mockHttpContextAccessor.Object, db, NullLogger<MobileCurrentUserAccessor>.Instance);

        // Act
        var result = await accessor.GetCurrentUserAsync();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCurrentUserAsync_ReturnsNull_WhenAuthorizationHeaderIsWhitespace()
    {
        // Arrange
        var db = CreateDbContext();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "   ";
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        var accessor = new MobileCurrentUserAccessor(mockHttpContextAccessor.Object, db, NullLogger<MobileCurrentUserAccessor>.Instance);

        // Act
        var result = await accessor.GetCurrentUserAsync();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCurrentUserAsync_ReturnsNull_WhenTokenNotFoundInDatabase()
    {
        // Arrange
        var db = CreateDbContext();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Bearer invalid-token-123";
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        var accessor = new MobileCurrentUserAccessor(mockHttpContextAccessor.Object, db, NullLogger<MobileCurrentUserAccessor>.Instance);

        // Act
        var result = await accessor.GetCurrentUserAsync();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCurrentUserAsync_ReturnsUser_WithValidBearerToken()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var apiToken = new ApiToken
        {
            UserId = user.Id,
            User = user,
            Token = "valid-token-abc123",
            Name = "Test Token",
            CreatedAt = DateTime.UtcNow
        };
        user.ApiTokens = new List<ApiToken> { apiToken };

        db.Users.Add(user);
        db.ApiTokens.Add(apiToken);
        await db.SaveChangesAsync();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Bearer valid-token-abc123";
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        var accessor = new MobileCurrentUserAccessor(mockHttpContextAccessor.Object, db, NullLogger<MobileCurrentUserAccessor>.Instance);

        // Act
        var result = await accessor.GetCurrentUserAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Id);
        Assert.Equal(user.UserName, result.UserName);
    }

    [Fact]
    public async Task GetCurrentUserAsync_ExtractsTokenFromBearerPrefix()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var apiToken = new ApiToken
        {
            UserId = user.Id,
            User = user,
            Token = "my-secret-token",
            Name = "Mobile Token",
            CreatedAt = DateTime.UtcNow
        };
        user.ApiTokens = new List<ApiToken> { apiToken };

        db.Users.Add(user);
        db.ApiTokens.Add(apiToken);
        await db.SaveChangesAsync();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Bearer my-secret-token";
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        var accessor = new MobileCurrentUserAccessor(mockHttpContextAccessor.Object, db, NullLogger<MobileCurrentUserAccessor>.Instance);

        // Act
        var result = await accessor.GetCurrentUserAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Id);
    }

    [Fact]
    public async Task GetCurrentUserAsync_HandlesTokenWithoutBearerPrefix()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var apiToken = new ApiToken
        {
            UserId = user.Id,
            User = user,
            Token = "plain-token",
            Name = "Plain Token",
            CreatedAt = DateTime.UtcNow
        };
        user.ApiTokens = new List<ApiToken> { apiToken };

        db.Users.Add(user);
        db.ApiTokens.Add(apiToken);
        await db.SaveChangesAsync();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "plain-token";
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        var accessor = new MobileCurrentUserAccessor(mockHttpContextAccessor.Object, db, NullLogger<MobileCurrentUserAccessor>.Instance);

        // Act
        var result = await accessor.GetCurrentUserAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Id);
    }

    [Fact]
    public async Task GetCurrentUserAsync_IncludesUserApiTokens()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var apiToken1 = new ApiToken
        {
            UserId = user.Id,
            User = user,
            Token = "token-1",
            Name = "Token 1",
            CreatedAt = DateTime.UtcNow
        };
        var apiToken2 = new ApiToken
        {
            UserId = user.Id,
            User = user,
            Token = "token-2",
            Name = "Token 2",
            CreatedAt = DateTime.UtcNow.AddDays(-1)
        };
        user.ApiTokens = new List<ApiToken> { apiToken1, apiToken2 };

        db.Users.Add(user);
        db.ApiTokens.AddRange(apiToken1, apiToken2);
        await db.SaveChangesAsync();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Bearer token-1";
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        var accessor = new MobileCurrentUserAccessor(mockHttpContextAccessor.Object, db, NullLogger<MobileCurrentUserAccessor>.Instance);

        // Act
        var result = await accessor.GetCurrentUserAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.ApiTokens);
        Assert.Equal(2, result.ApiTokens.Count);
    }

    #endregion

    #region Caching Tests

    [Fact]
    public async Task GetCurrentUserAsync_CachesResultOnFirstCall()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var apiToken = new ApiToken
        {
            UserId = user.Id,
            User = user,
            Token = "cached-token",
            Name = "Cached Token",
            CreatedAt = DateTime.UtcNow
        };
        user.ApiTokens = new List<ApiToken> { apiToken };

        db.Users.Add(user);
        db.ApiTokens.Add(apiToken);
        await db.SaveChangesAsync();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Bearer cached-token";
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        var accessor = new MobileCurrentUserAccessor(mockHttpContextAccessor.Object, db, NullLogger<MobileCurrentUserAccessor>.Instance);

        // Act
        var result1 = await accessor.GetCurrentUserAsync();
        var result2 = await accessor.GetCurrentUserAsync();

        // Assert
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Same(result1, result2); // Same instance (cached)
    }

    [Fact]
    public async Task GetCurrentUserAsync_CachesNullResult()
    {
        // Arrange
        var db = CreateDbContext();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Bearer non-existent";
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        var accessor = new MobileCurrentUserAccessor(mockHttpContextAccessor.Object, db, NullLogger<MobileCurrentUserAccessor>.Instance);

        // Act
        var result1 = await accessor.GetCurrentUserAsync();

        // Add token after first call
        var user = TestDataFixtures.CreateUser();
        var apiToken = new ApiToken
        {
            UserId = user.Id,
            User = user,
            Token = "non-existent",
            Name = "Late Token",
            CreatedAt = DateTime.UtcNow
        };
        user.ApiTokens = new List<ApiToken> { apiToken };
        db.Users.Add(user);
        db.ApiTokens.Add(apiToken);
        await db.SaveChangesAsync();

        var result2 = await accessor.GetCurrentUserAsync();

        // Assert - should still return null (cached)
        Assert.Null(result1);
        Assert.Null(result2);
    }

    #endregion

    #region Reset Tests

    [Fact]
    public async Task Reset_ClearsCachedUser()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var apiToken = new ApiToken
        {
            UserId = user.Id,
            User = user,
            Token = "reset-test-token",
            Name = "Reset Test Token",
            CreatedAt = DateTime.UtcNow
        };
        user.ApiTokens = new List<ApiToken> { apiToken };

        db.Users.Add(user);
        db.ApiTokens.Add(apiToken);
        await db.SaveChangesAsync();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Bearer reset-test-token";
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        var accessor = new MobileCurrentUserAccessor(mockHttpContextAccessor.Object, db, NullLogger<MobileCurrentUserAccessor>.Instance);

        // Act
        var result1 = await accessor.GetCurrentUserAsync();
        accessor.Reset();
        var result2 = await accessor.GetCurrentUserAsync();

        // Assert
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        // After reset, should fetch from database again (not same cached instance)
        // Both should be equal but potentially different instances
        Assert.Equal(result1.Id, result2.Id);
    }

    [Fact]
    public async Task Reset_AllowsNewTokenResolution()
    {
        // Arrange
        var db = CreateDbContext();
        var user1 = TestDataFixtures.CreateUser();
        var apiToken1 = new ApiToken
        {
            UserId = user1.Id,
            User = user1,
            Token = "token-user-1",
            Name = "User 1 Token",
            CreatedAt = DateTime.UtcNow
        };
        user1.ApiTokens = new List<ApiToken> { apiToken1 };

        var user2 = TestDataFixtures.CreateUser();
        var apiToken2 = new ApiToken
        {
            UserId = user2.Id,
            User = user2,
            Token = "token-user-2",
            Name = "User 2 Token",
            CreatedAt = DateTime.UtcNow
        };
        user2.ApiTokens = new List<ApiToken> { apiToken2 };

        db.Users.AddRange(user1, user2);
        db.ApiTokens.AddRange(apiToken1, apiToken2);
        await db.SaveChangesAsync();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Bearer token-user-1";
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        var accessor = new MobileCurrentUserAccessor(mockHttpContextAccessor.Object, db, NullLogger<MobileCurrentUserAccessor>.Instance);

        // Act
        var result1 = await accessor.GetCurrentUserAsync();

        // Change token
        httpContext.Request.Headers["Authorization"] = "Bearer token-user-2";
        accessor.Reset();

        var result2 = await accessor.GetCurrentUserAsync();

        // Assert
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Equal(user1.Id, result1.Id);
        Assert.Equal(user2.Id, result2.Id);
    }

    #endregion

    #region Hashed Token Tests

    [Fact]
    public async Task GetCurrentUserAsync_ReturnsUser_WithHashedWayfarerToken()
    {
        // Arrange - Wayfarer tokens have TokenHash set and Token is null
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var plainToken = "wf_test-hashed-token-abc123";
        var apiToken = new ApiToken
        {
            UserId = user.Id,
            User = user,
            Token = null, // Wayfarer tokens don't store plain text
            TokenHash = ApiTokenService.HashToken(plainToken),
            Name = "Wayfarer Mobile Token",
            CreatedAt = DateTime.UtcNow
        };
        user.ApiTokens = new List<ApiToken> { apiToken };

        db.Users.Add(user);
        db.ApiTokens.Add(apiToken);
        await db.SaveChangesAsync();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = $"Bearer {plainToken}";
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        var accessor = new MobileCurrentUserAccessor(mockHttpContextAccessor.Object, db, NullLogger<MobileCurrentUserAccessor>.Instance);

        // Act
        var result = await accessor.GetCurrentUserAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Id);
        Assert.Equal(user.UserName, result.UserName);
    }

    [Fact]
    public async Task GetCurrentUserAsync_ReturnsNull_WhenHashedTokenDoesNotMatch()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var apiToken = new ApiToken
        {
            UserId = user.Id,
            User = user,
            Token = null,
            TokenHash = ApiTokenService.HashToken("wf_correct-token"),
            Name = "Hashed Token",
            CreatedAt = DateTime.UtcNow
        };
        user.ApiTokens = new List<ApiToken> { apiToken };

        db.Users.Add(user);
        db.ApiTokens.Add(apiToken);
        await db.SaveChangesAsync();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Bearer wf_wrong-token";
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        var accessor = new MobileCurrentUserAccessor(mockHttpContextAccessor.Object, db, NullLogger<MobileCurrentUserAccessor>.Instance);

        // Act
        var result = await accessor.GetCurrentUserAsync();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCurrentUserAsync_PrefersHashedTokenOverPlainText()
    {
        // Arrange - Token has both Token and TokenHash set (edge case during migration)
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var plainToken = "wf_dual-token";
        var apiToken = new ApiToken
        {
            UserId = user.Id,
            User = user,
            Token = "different-plain-value", // Different plain value
            TokenHash = ApiTokenService.HashToken(plainToken), // Hash matches what client sends
            Name = "Dual Token",
            CreatedAt = DateTime.UtcNow
        };
        user.ApiTokens = new List<ApiToken> { apiToken };

        db.Users.Add(user);
        db.ApiTokens.Add(apiToken);
        await db.SaveChangesAsync();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = $"Bearer {plainToken}";
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        var accessor = new MobileCurrentUserAccessor(mockHttpContextAccessor.Object, db, NullLogger<MobileCurrentUserAccessor>.Instance);

        // Act
        var result = await accessor.GetCurrentUserAsync();

        // Assert - Should match via TokenHash
        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Id);
    }

    [Fact]
    public async Task GetCurrentUserAsync_FallsBackToPlainToken_WhenHashNotSet()
    {
        // Arrange - Third-party tokens only have Token set (no hash)
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var apiToken = new ApiToken
        {
            UserId = user.Id,
            User = user,
            Token = "third-party-api-key-123",
            TokenHash = null, // No hash for third-party tokens
            Name = "Mapbox",
            CreatedAt = DateTime.UtcNow
        };
        user.ApiTokens = new List<ApiToken> { apiToken };

        db.Users.Add(user);
        db.ApiTokens.Add(apiToken);
        await db.SaveChangesAsync();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Bearer third-party-api-key-123";
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        var accessor = new MobileCurrentUserAccessor(mockHttpContextAccessor.Object, db, NullLogger<MobileCurrentUserAccessor>.Instance);

        // Act
        var result = await accessor.GetCurrentUserAsync();

        // Assert - Should match via plain Token
        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Id);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task GetCurrentUserAsync_HandlesMultipleSpacesInAuthHeader()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var apiToken = new ApiToken
        {
            UserId = user.Id,
            User = user,
            Token = "spaced-token",
            Name = "Spaced Token",
            CreatedAt = DateTime.UtcNow
        };
        user.ApiTokens = new List<ApiToken> { apiToken };

        db.Users.Add(user);
        db.ApiTokens.Add(apiToken);
        await db.SaveChangesAsync();

        var httpContext = new DefaultHttpContext();
        // Multiple spaces - LastOrDefault should get last part
        httpContext.Request.Headers["Authorization"] = "Bearer  spaced-token";
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        var accessor = new MobileCurrentUserAccessor(mockHttpContextAccessor.Object, db, NullLogger<MobileCurrentUserAccessor>.Instance);

        // Act
        var result = await accessor.GetCurrentUserAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Id);
    }

    [Fact]
    public async Task GetCurrentUserAsync_ReturnsNull_WhenUserNoLongerExists()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var apiToken = new ApiToken
        {
            UserId = user.Id,
            User = user,
            Token = "orphan-token",
            Name = "Orphan Token",
            CreatedAt = DateTime.UtcNow
        };
        user.ApiTokens = new List<ApiToken> { apiToken };

        db.Users.Add(user);
        db.ApiTokens.Add(apiToken);
        await db.SaveChangesAsync();

        // Remove the user but keep the token (simulating deleted user)
        db.Users.Remove(user);
        await db.SaveChangesAsync();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Bearer orphan-token";
        var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        mockHttpContextAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        var accessor = new MobileCurrentUserAccessor(mockHttpContextAccessor.Object, db, NullLogger<MobileCurrentUserAccessor>.Instance);

        // Act
        var result = await accessor.GetCurrentUserAsync();

        // Assert
        Assert.Null(result);
    }

    #endregion
}
