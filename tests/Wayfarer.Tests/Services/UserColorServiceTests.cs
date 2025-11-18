using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests for the UserColorService which generates consistent colors for users.
/// </summary>
public class UserColorServiceTests
{
    private readonly UserColorService _service = new();

    [Fact]
    public void GetColorHex_ReturnsSameColor_ForSameKey()
    {
        // Arrange & Act
        var first = _service.GetColorHex("alice");
        var second = _service.GetColorHex("alice");

        // Assert
        Assert.Equal(first, second);
    }

    [Fact]
    public void GetColorHex_UsesFallback_ForEmptyKey()
    {
        // Arrange
        var fallback = _service.GetColorHex("user");

        // Act
        var result = _service.GetColorHex(string.Empty);

        // Assert
        Assert.Equal(fallback, result);
    }

    [Fact]
    public void GetColorHex_ProducesDistinctColors_ForDifferentKeys()
    {
        // Arrange & Act
        var alice = _service.GetColorHex("alice");
        var bob = _service.GetColorHex("bob");

        // Assert
        Assert.NotEqual(alice, bob);
    }
}
