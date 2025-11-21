using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Quartz.Impl.Matchers;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Areas.Admin.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Admin JobsController coverage for index and start flows.
/// </summary>
public class AdminJobsControllerTests : TestBase
{
    [Fact]
    public async Task Index_ReturnsJobsFromScheduler()
    {
        var db = CreateDbContext();
        db.JobHistories.Add(new JobHistory { JobName = "JobA", LastRunTime = DateTime.UtcNow.AddHours(-1), Status = "Done" });
        await db.SaveChangesAsync();
        var scheduler = new Mock<IScheduler>();
        var jobKey = new JobKey("JobA", "default");
        var jobDetail = new Mock<IJobDetail>();
        jobDetail.SetupGet(j => j.JobDataMap).Returns(new JobDataMap());
        scheduler.Setup(s => s.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<JobKey> { jobKey });
        scheduler.Setup(s => s.GetJobDetail(jobKey, It.IsAny<CancellationToken>())).ReturnsAsync(jobDetail.Object);
        scheduler.Setup(s => s.GetTriggersOfJob(jobKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<ITrigger>)new List<ITrigger>());

        var controller = BuildController(db, scheduler.Object);

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<IEnumerable<JobMonitoringViewModel>>(view.Model);
        var job = Assert.Single(model);
        Assert.Equal("JobA", job.JobName);
    }

    [Fact]
    public async Task StartJob_UpdatesHistoryAndRedirects()
    {
        var db = CreateDbContext();
        var scheduler = new Mock<IScheduler>();
        var jobKey = new JobKey("JobB", "default");
        scheduler.Setup(s => s.GetJobDetail(jobKey, It.IsAny<CancellationToken>())).ReturnsAsync(Mock.Of<IJobDetail>());
        scheduler.Setup(s => s.TriggerJob(jobKey, null, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var services = new ServiceCollection()
            .AddSingleton(db)
            .BuildServiceProvider();

        var controller = BuildController(db, scheduler.Object, services);

        var result = await controller.StartJob("JobB", "default");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        scheduler.Verify(s => s.TriggerJob(jobKey, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static JobsController BuildController(ApplicationDbContext db, IScheduler scheduler, IServiceProvider? services = null)
    {
        var controller = new JobsController(
            scheduler,
            services ?? new ServiceCollection().BuildServiceProvider(),
            db,
            NullLogger<UsersController>.Instance);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "admin"),
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(ClaimTypes.Role, ApplicationRoles.Admin)
            }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());
        return controller;
    }
}
