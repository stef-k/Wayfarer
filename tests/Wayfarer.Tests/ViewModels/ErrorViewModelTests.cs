using Xunit;

namespace Wayfarer.Tests.ViewModels;

/// <summary>
/// Tests for ErrorViewModel computed property ShowRequestId.
/// </summary>
public class ErrorViewModelTests
{
    [Fact]
    public void ShowRequestId_ReturnsTrue_WhenRequestIdIsSet()
    {
        // Arrange
        var model = new Wayfarer.Models.ErrorViewModel
        {
            RequestId = "12345-request-id"
        };

        // Act
        var result = model.ShowRequestId;

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ShowRequestId_ReturnsFalse_WhenRequestIdIsNull()
    {
        // Arrange
        var model = new Wayfarer.Models.ErrorViewModel
        {
            RequestId = null
        };

        // Act
        var result = model.ShowRequestId;

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ShowRequestId_ReturnsFalse_WhenRequestIdIsEmpty()
    {
        // Arrange
        var model = new Wayfarer.Models.ErrorViewModel
        {
            RequestId = string.Empty
        };

        // Act
        var result = model.ShowRequestId;

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ShowRequestId_ReturnsFalse_WhenRequestIdIsWhitespace()
    {
        // Arrange
        var model = new Wayfarer.Models.ErrorViewModel
        {
            RequestId = "   "
        };

        // Act
        var result = model.ShowRequestId;

        // Assert
        Assert.True(result);
    }
}
