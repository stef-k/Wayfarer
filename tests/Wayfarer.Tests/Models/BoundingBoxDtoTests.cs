using Wayfarer.Models.Dtos;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>
/// Tests for <see cref="BoundingBoxDto"/>.
/// </summary>
public class BoundingBoxDtoTests
{
    [Fact]
    public void GetAreaSquareDegrees_ComputesAbsoluteArea()
    {
        var dto = new BoundingBoxDto { North = 10, South = 5, East = 20, West = 10 };

        var area = dto.GetAreaSquareDegrees();

        Assert.Equal(50, area);
    }

    [Fact]
    public void Contains_ReturnsTrue_WhenInsideBounds()
    {
        var dto = new BoundingBoxDto { North = 10, South = 5, East = 20, West = 10 };

        Assert.True(dto.Contains(7.5, 15));
    }

    [Fact]
    public void Contains_ReturnsFalse_WhenOutsideBounds()
    {
        var dto = new BoundingBoxDto { North = 10, South = 5, East = 20, West = 10 };

        Assert.False(dto.Contains(4.9, 15));   // below south
        Assert.False(dto.Contains(7.5, 20.1)); // east of east
    }

    [Fact]
    public void ToString_FormatsFourDecimals()
    {
        var dto = new BoundingBoxDto { North = 1.23456, South = -1.23456, East = 10.0, West = -10.0 };

        var text = dto.ToString();

        Assert.Equal("N:1.2346, S:-1.2346, E:10.0000, W:-10.0000", text);
    }
}
