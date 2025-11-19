using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>
/// Tests for <see cref="ViewHelpers"/> utility methods.
/// </summary>
public class ViewHelpersTests
{
    [Fact]
    public void GetValueOrFallback_ReturnsValue_WhenNotNull()
    {
        // Arrange
        var value = "Hello World";

        // Act
        var result = ViewHelpers.GetValueOrFallback(value);

        // Assert
        Assert.Equal("Hello World", result);
    }

    [Fact]
    public void GetValueOrFallback_ReturnsFallback_WhenNull()
    {
        // Arrange
        object? value = null;

        // Act
        var result = ViewHelpers.GetValueOrFallback(value!);

        // Assert
        Assert.Equal("N/A", result);
    }

    [Fact]
    public void GetValueOrFallback_ReturnsFallback_WhenEmptyString()
    {
        // Arrange
        var value = "";

        // Act
        var result = ViewHelpers.GetValueOrFallback(value);

        // Assert
        Assert.Equal("N/A", result);
    }

    [Fact]
    public void GetValueOrFallback_ReturnsFallback_WhenWhitespaceString()
    {
        // Arrange
        var value = "   ";

        // Act
        var result = ViewHelpers.GetValueOrFallback(value);

        // Assert
        Assert.Equal("N/A", result);
    }

    [Fact]
    public void GetValueOrFallback_UsesCustomFallback()
    {
        // Arrange
        object? value = null;

        // Act
        var result = ViewHelpers.GetValueOrFallback(value!, "Unknown");

        // Assert
        Assert.Equal("Unknown", result);
    }

    [Fact]
    public void GetValueOrFallback_ReturnsValue_ForIntegerType()
    {
        // Arrange
        var value = 42;

        // Act
        var result = ViewHelpers.GetValueOrFallback(value);

        // Assert
        Assert.Equal("42", result);
    }

    [Fact]
    public void GetValueOrFallback_ReturnsValue_ForDateTimeType()
    {
        // Arrange
        var value = new DateTime(2024, 6, 15);

        // Act
        var result = ViewHelpers.GetValueOrFallback(value);

        // Assert - Check for either full year or short year format
        Assert.True(result.Contains("2024") || result.Contains("24"),
            $"Expected date string to contain year, got: {result}");
    }

    [Fact]
    public void GetValueOrFallback_ReturnsValue_ForBooleanType()
    {
        // Arrange
        var value = true;

        // Act
        var result = ViewHelpers.GetValueOrFallback(value);

        // Assert
        Assert.Equal("True", result);
    }

    [Fact]
    public void GetValueOrFallback_ReturnsValue_ForDecimalType()
    {
        // Arrange
        var value = 123.45m;

        // Act
        var result = ViewHelpers.GetValueOrFallback(value);

        // Assert
        Assert.Equal("123.45", result);
    }

    [Fact]
    public void GetValueOrFallback_ReturnsValue_ForGuidType()
    {
        // Arrange
        var value = Guid.Parse("12345678-1234-1234-1234-123456789012");

        // Act
        var result = ViewHelpers.GetValueOrFallback(value);

        // Assert
        Assert.Contains("12345678", result);
    }
}
