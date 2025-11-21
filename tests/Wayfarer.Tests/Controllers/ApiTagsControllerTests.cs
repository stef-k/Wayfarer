using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// API tags suggestion/popular endpoints.
/// </summary>
public class ApiTagsControllerTests : TestBase
{
    [Fact]
    public async Task Suggest_UsesService()
    {
        var svc = new Mock<ITripTagService>();
        svc.Setup(s => s.GetSuggestionsAsync("hi", 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TagSuggestionDto> { new("Hike", "hi", 1) });
        var controller = new TagsController(CreateDbContext(), NullLogger<BaseApiController>.Instance, svc.Object);

        var result = await controller.Suggest("hi", 5, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<IEnumerable<TagSuggestionDto>>(ok.Value);
        Assert.Single(items);
    }

    [Fact]
    public async Task Popular_ClampsTake()
    {
        var svc = new Mock<ITripTagService>();
        svc.Setup(s => s.GetPopularAsync(50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TagSuggestionDto>());
        var controller = new TagsController(CreateDbContext(), NullLogger<BaseApiController>.Instance, svc.Object);

        var result = await controller.Popular(500, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        svc.Verify(s => s.GetPopularAsync(50, It.IsAny<CancellationToken>()), Times.Once);
    }
}
