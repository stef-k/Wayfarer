using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests;

public class UserColorServiceTests
{
    private readonly UserColorService _service = new();

    [Fact]
    public void GetColorHex_ReturnsSameColor_ForSameKey()
    {
        var first = _service.GetColorHex("alice");
        var second = _service.GetColorHex("alice");

        Assert.Equal(first, second);
    }

    [Fact]
    public void GetColorHex_UsesFallback_ForEmptyKey()
    {
        var fallback = _service.GetColorHex("user");
        var result = _service.GetColorHex(string.Empty);

        Assert.Equal(fallback, result);
    }

    [Fact]
    public void GetColorHex_ProducesDistinctColors_ForDifferentKeys()
    {
        var alice = _service.GetColorHex("alice");
        var bob = _service.GetColorHex("bob");

        Assert.NotEqual(alice, bob);
    }
}
