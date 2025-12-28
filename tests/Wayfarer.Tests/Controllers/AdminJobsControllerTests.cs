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
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Admin JobsController coverage for index, start, pause, resume, and cancel flows.
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
        jobDetail.SetupGet(j => j.Key).Returns(jobKey);
        scheduler.Setup(s => s.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<JobKey> { jobKey });
        scheduler.Setup(s => s.GetJobDetail(jobKey, It.IsAny<CancellationToken>())).ReturnsAsync(jobDetail.Object);
        scheduler.Setup(s => s.GetTriggersOfJob(jobKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<ITrigger>)new List<ITrigger>());
        scheduler.Setup(s => s.GetCurrentlyExecutingJobs(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<IJobExecutionContext>)new List<IJobExecutionContext>());

        var controller = BuildController(db, scheduler.Object);

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<IEnumerable<JobMonitoringViewModel>>(view.Model);
        var job = Assert.Single(model);
        Assert.Equal("JobA", job.JobName);
        Assert.False(job.IsRunning);
        Assert.False(job.IsPaused);
        Assert.True(job.IsInterruptable);
    }

    [Fact]
    public async Task Index_DetectsRunningJob()
    {
        var db = CreateDbContext();
        var scheduler = new Mock<IScheduler>();
        var jobKey = new JobKey("RunningJob", "default");
        var jobDetail = new Mock<IJobDetail>();
        jobDetail.SetupGet(j => j.JobDataMap).Returns(new JobDataMap());
        jobDetail.SetupGet(j => j.Key).Returns(jobKey);

        var execContext = new Mock<IJobExecutionContext>();
        execContext.SetupGet(e => e.JobDetail).Returns(jobDetail.Object);

        scheduler.Setup(s => s.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<JobKey> { jobKey });
        scheduler.Setup(s => s.GetJobDetail(jobKey, It.IsAny<CancellationToken>())).ReturnsAsync(jobDetail.Object);
        scheduler.Setup(s => s.GetTriggersOfJob(jobKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<ITrigger>)new List<ITrigger>());
        scheduler.Setup(s => s.GetCurrentlyExecutingJobs(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<IJobExecutionContext>)new List<IJobExecutionContext> { execContext.Object });

        var controller = BuildController(db, scheduler.Object);

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<IEnumerable<JobMonitoringViewModel>>(view.Model);
        var job = Assert.Single(model);
        Assert.True(job.IsRunning);
        Assert.Equal("Running", job.Status);
    }

    [Fact]
    public async Task Index_DetectsPausedJob()
    {
        var db = CreateDbContext();
        var scheduler = new Mock<IScheduler>();
        var jobKey = new JobKey("PausedJob", "default");
        var triggerKey = new TriggerKey("PausedTrigger", "default");
        var trigger = new Mock<ITrigger>();
        trigger.SetupGet(t => t.Key).Returns(triggerKey);
        var jobDetail = new Mock<IJobDetail>();
        jobDetail.SetupGet(j => j.JobDataMap).Returns(new JobDataMap());
        jobDetail.SetupGet(j => j.Key).Returns(jobKey);

        scheduler.Setup(s => s.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<JobKey> { jobKey });
        scheduler.Setup(s => s.GetJobDetail(jobKey, It.IsAny<CancellationToken>())).ReturnsAsync(jobDetail.Object);
        scheduler.Setup(s => s.GetTriggersOfJob(jobKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<ITrigger>)new List<ITrigger> { trigger.Object });
        scheduler.Setup(s => s.GetTriggerState(triggerKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(TriggerState.Paused);
        scheduler.Setup(s => s.GetCurrentlyExecutingJobs(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<IJobExecutionContext>)new List<IJobExecutionContext>());

        var controller = BuildController(db, scheduler.Object);

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<IEnumerable<JobMonitoringViewModel>>(view.Model);
        var job = Assert.Single(model);
        Assert.True(job.IsPaused);
        Assert.Equal("Paused", job.Status);
    }

    [Fact]
    public async Task StartJob_UpdatesHistoryAndRedirects()
    {
        var db = CreateDbContext();
        var scheduler = new Mock<IScheduler>();
        var jobKey = new JobKey("JobB", "default");
        scheduler.Setup(s => s.GetJobDetail(jobKey, It.IsAny<CancellationToken>())).ReturnsAsync(Mock.Of<IJobDetail>());
        scheduler.Setup(s => s.TriggerJob(jobKey, It.IsAny<JobDataMap>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var services = new ServiceCollection()
            .AddSingleton(db)
            .BuildServiceProvider();

        var controller = BuildController(db, scheduler.Object, services);

        var result = await controller.StartJob("JobB", "default");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        scheduler.Verify(s => s.TriggerJob(jobKey, It.IsAny<JobDataMap>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PauseJob_CallsSchedulerPauseAndRedirects()
    {
        var db = CreateDbContext();
        var scheduler = new Mock<IScheduler>();
        var jobKey = new JobKey("JobC", "default");
        scheduler.Setup(s => s.PauseJob(jobKey, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var controller = BuildController(db, scheduler.Object);

        var result = await controller.PauseJob("JobC", "default");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        scheduler.Verify(s => s.PauseJob(jobKey, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("paused", controller.TempData["Message"]?.ToString() ?? "");
    }

    [Fact]
    public async Task ResumeJob_CallsSchedulerResumeAndRedirects()
    {
        var db = CreateDbContext();
        var scheduler = new Mock<IScheduler>();
        var jobKey = new JobKey("JobD", "default");
        scheduler.Setup(s => s.ResumeJob(jobKey, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var controller = BuildController(db, scheduler.Object);

        var result = await controller.ResumeJob("JobD", "default");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        scheduler.Verify(s => s.ResumeJob(jobKey, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("resumed", controller.TempData["Message"]?.ToString() ?? "");
    }

    [Fact]
    public async Task CancelJob_WhenInterrupted_ShowsSuccessMessage()
    {
        var db = CreateDbContext();
        var scheduler = new Mock<IScheduler>();
        var jobKey = new JobKey("JobE", "default");
        scheduler.Setup(s => s.Interrupt(jobKey, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var controller = BuildController(db, scheduler.Object);

        var result = await controller.CancelJob("JobE", "default");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        scheduler.Verify(s => s.Interrupt(jobKey, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("cancellation requested", controller.TempData["Message"]?.ToString() ?? "");
    }

    [Fact]
    public async Task CancelJob_WhenNotInterrupted_ShowsErrorMessage()
    {
        var db = CreateDbContext();
        var scheduler = new Mock<IScheduler>();
        var jobKey = new JobKey("JobF", "default");
        scheduler.Setup(s => s.Interrupt(jobKey, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var controller = BuildController(db, scheduler.Object);

        var result = await controller.CancelJob("JobF", "default");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Contains("could not be cancelled", controller.TempData["Error"]?.ToString() ?? "");
    }

    private static JobsController BuildController(ApplicationDbContext db, IScheduler scheduler, IServiceProvider? services = null)
    {
        var controller = new JobsController(
            scheduler,
            services ?? new ServiceCollection().BuildServiceProvider(),
            db,
            NullLogger<UsersController>.Instance,
            new SseService());
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
