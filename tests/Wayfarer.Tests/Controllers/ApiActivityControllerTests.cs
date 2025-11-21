using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// API Activity types listing.
/// </summary>
public class ApiActivityControllerTests : TestBase
{
    [Fact]
    public void GetActivityTypes_ReturnsOrderedActivities()
    {
        var db = CreateDbContext();
        db.ActivityTypes.AddRange(
            new ActivityType { Id = 2, Name = "Walk" },
            new ActivityType { Id = 1, Name = "Bike" });
        db.SaveChanges();
        var controller = new ActivityController(db, NullLogger<BaseApiController>.Instance);

        var result = controller.GetActivityTypes();

        var ok = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsAssignableFrom<IEnumerable<ActivityTypeDto>>(ok.Value);
        Assert.Equal(new[] { "Bike", "Walk" }, list.Select(a => a.Name).ToArray());
    }
}
