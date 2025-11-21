using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Public.Controllers;
using Wayfarer.Models.Dtos;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Public tags suggestions/popular endpoints.
/// </summary>
public class TagsControllerTests : TestBase
{
    [Fact]
    public async Task Suggest_ReturnsJson_ItemsFromService()
    {
        var db = CreateDbContext();
        var svc = new Mock<ITripTagService>();
        svc.Setup(s => s.GetSuggestionsAsync("hike", 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TagSuggestionDto> { new("Hike", "hike", 1) });
        var controller = BuildController(db, svc.Object);

        var result = await controller.Suggest("hike", 5, CancellationToken.None);

        var json = Assert.IsType<JsonResult>(result);
        var items = Assert.IsAssignableFrom<IEnumerable<TagSuggestionDto>>(json.Value!);
        Assert.Single(items);
        Assert.Equal("hike", items.First().Slug);
    }

    [Fact]
    public async Task Popular_ReturnsJson_ItemsFromService()
    {
        var db = CreateDbContext();
        var svc = new Mock<ITripTagService>();
        svc.Setup(s => s.GetPopularAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TagSuggestionDto> { new("Top", "top", 3) });
        var controller = BuildController(db, svc.Object);

        var result = await controller.Popular(10, CancellationToken.None);

        var json = Assert.IsType<JsonResult>(result);
        var items = Assert.IsAssignableFrom<IEnumerable<TagSuggestionDto>>(json.Value!);
        Assert.Single(items);
        Assert.Equal("top", items.First().Slug);
    }

    private static TagsController BuildController(ApplicationDbContext db, ITripTagService svc)
    {
        return new TagsController(NullLogger<TagsController>.Instance, db, svc);
    }
}
