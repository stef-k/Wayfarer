using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Quartz;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Middleware;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationImports;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Middleware;

/// <summary>Runs actual MVC upload endpoints through Kestrel, authorization, antiforgery and form binding.</summary>
public sealed class UploadRequestBoundaryTests : TestBase
{
    /// <summary>Unauthorized, disabled and oversized Location uploads never create durable state.</summary>
    [Theory]
    [InlineData(false, 1, 64, false, 401)]
    [InlineData(true, -1, 64, false, 403)]
    [InlineData(true, 1, 1048577, false, 413)]
    [InlineData(true, 1, 1048577, true, 413)]
    [InlineData(true, 1, 64, false, 302)]
    public async Task LocationUpload_EnforcesBeforeStaging(bool authenticated, int limit, int size, bool chunked, int status)
    {
        var db = CreateDbContext();
        var staging = ImportStaging.Create(CreateTestDirectory());
        var settings = Settings(limit);
        using var host = await StartHost(db, staging, settings, Mock.Of<ITripImportService>());
        using var client = Client(host, authenticated);
        var token = await client.GetStringAsync("/test-antiforgery");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(token), "__RequestVerificationToken");
        form.Add(new StringContent("Csv"), "FileType");
        form.Add(new ByteArrayContent(new byte[size]), "File", "history.csv");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/User/LocationImport/Upload") { Content = form };
        if (chunked) request.Headers.TransferEncodingChunked = true;
        using var response = await client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        settings.Verify(s => s.GetSettings(), authenticated ? Times.Once() : Times.Never());
        Assert.Equal(status == 302 ? 1 : 0, db.LocationImports.Count());
        Assert.Equal(status == 302 ? 1 : 0, Directory.Exists(staging.DirectoryPath)
            ? Directory.GetFiles(staging.DirectoryPath).Length : 0);
    }

    /// <summary>The parser-derived section ceiling rejects before the Trip import service is invoked.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TripUpload_RespectsSectionLimit(bool oversized)
    {
        var service = new Mock<ITripImportService>();
        service.Setup(s => s.ImportWayfarerKmlAsync(It.IsAny<Stream>(), "upload-user", TripImportMode.Auto,
            It.IsAny<CancellationToken>())).ReturnsAsync(new TripImportResult(Guid.NewGuid(), []));
        using var host = await StartHost(CreateDbContext(), ImportStaging.Create(CreateTestDirectory()), Settings(100), service.Object);
        using var client = Client(host, true);
        client.DefaultRequestHeaders.Add("RequestVerificationToken", await client.GetStringAsync("/test-antiforgery"));
        // A sparse task-owned file streams through HttpClient; no giant in-memory corpus is retained.
        var path = Path.Combine(CreateTestDirectory(), "trip.kml");
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        file.SetLength(oversized ? WayfarerKmlParser.MaximumEncodedDocumentBytes + 1 : 64);
        using var form = new MultipartFormDataContent();
        form.Add(new StreamContent(file), "file", "trip.kml");
        using var response = await client.PostAsync("/User/Trip/Import", form);
        Assert.Equal(oversized ? HttpStatusCode.BadRequest : HttpStatusCode.OK, response.StatusCode);
        service.Verify(s => s.ImportWayfarerKmlAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<TripImportMode>(),
            It.IsAny<CancellationToken>()), oversized ? Times.Never() : Times.Once());
    }

    /// <summary>Only the two production POST actions opt in; Trip alone adds the existing parser budget.</summary>
    [Fact]
    public async Task Metadata_MarksExactlyTheTwoUploads()
    {
        using var host = await StartHost(CreateDbContext(), ImportStaging.Create(CreateTestDirectory()), Settings(1), Mock.Of<ITripImportService>());
        var actions = host.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>().Where(a => a.EndpointMetadata.OfType<UserFileUploadAttribute>().Any()).ToArray();
        Assert.Equal(2, actions.Length);
        var trip = Assert.Single(actions, a => a.ControllerTypeInfo.AsType() == typeof(TripImportController));
        var location = Assert.Single(actions, a => a.ControllerTypeInfo.AsType() == typeof(LocationImportController));
        Assert.Equal("Import", trip.ActionName);
        Assert.Equal("Upload", location.ActionName);
        var section = trip.MethodInfo.GetCustomAttributes(typeof(RequestFormLimitsAttribute), false).Cast<RequestFormLimitsAttribute>().Single();
        Assert.Equal(4 * WayfarerKmlParser.MaximumDocumentCharacters + 4, section.MultipartBodyLengthLimit);
        Assert.Empty(location.MethodInfo.GetCustomAttributes(typeof(RequestFormLimitsAttribute), false));
    }

    /// <summary>Body-consuming errors retain their response when the same Kestrel request is re-executed.</summary>
    [Theory]
    [InlineData(false, 422)]
    [InlineData(true, 500)]
    public async Task GlobalCeiling_AllowsSafeErrorReExecution(bool throwException, int expectedStatus)
    {
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseKestrel()
            .UseUrls("http://127.0.0.1:0").ConfigureServices(services => services.AddRouting())
            .Configure(app =>
            {
                // Match production ordering: both error handlers re-enter the size middleware.
                app.UseExceptionHandler("/Home/Error");
                app.UseStatusCodePagesWithReExecute("/Error/{0}");
                app.UseMiddleware<DynamicRequestSizeMiddleware>();
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapPost("/consume", async context =>
                    {
                        await context.Request.Body.CopyToAsync(Stream.Null);
                        if (throwException) throw new InvalidOperationException("Controlled request failure");
                        context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                    });
                    endpoints.Map("/Home/Error", ReportActiveCeiling);
                    endpoints.Map("/Error/{status}", ReportActiveCeiling);
                });
            })).StartAsync();
        using var client = Client(host, false);
        using var response = await client.PostAsync("/consume", new StringContent("read before error"));
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal("True:104857600", await response.Content.ReadAsStringAsync());
    }

    /// <summary>Proves the rerouted handler was reached with Kestrel's already-enforced read-only ceiling.</summary>
    private static Task ReportActiveCeiling(HttpContext context)
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>()!;
        return context.Response.WriteAsync($"{feature.IsReadOnly}:{feature.MaxRequestBodySize}");
    }

    /// <summary>Supplies the admin policy and observes authorization-before-settings ordering.</summary>
    private static Mock<IApplicationSettingsService> Settings(int limit)
    {
        var settings = new Mock<IApplicationSettingsService>();
        settings.Setup(s => s.GetSettings()).Returns(new ApplicationSettings { UploadSizeLimitMB = limit });
        return settings;
    }

    /// <summary>Uses a real host body feature, application controllers and the production middleware order.</summary>
    private static Task<IHost> StartHost(ApplicationDbContext db, LocationImportStagedFiles staging,
        Mock<IApplicationSettingsService> settings, ITripImportService trips) => new HostBuilder()
        .ConfigureWebHost(web => web.UseKestrel().UseUrls("http://127.0.0.1:0").ConfigureServices(services =>
        {
            services.AddSingleton(db);
            services.AddSingleton(staging);
            services.AddSingleton(settings.Object);
            services.AddSingleton(trips);
            services.AddSingleton(Mock.Of<IScheduler>());
            services.AddSingleton(Mock.Of<ILocationEnrichmentPresentationProjector>());
            services.AddSingleton(Mock.Of<ILocationImportLifecycle>());
            services.AddControllersWithViews().AddApplicationPart(typeof(TripImportController).Assembly);
            services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = 401;
                    return Task.CompletedTask;
                });
        }).Configure(app =>
        {
            app.UseMiddleware<DynamicRequestSizeMiddleware>();
            app.UseRouting();
            app.UseAuthentication();
            app.Use(async (context, next) =>
            {
                if (context.Request.Headers.ContainsKey("X-Test-User"))
                    context.User = BuildHttpContextWithUser("upload-user").User;
                if (context.Request.Path == "/test-antiforgery")
                {
                    var tokens = context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context);
                    await context.Response.WriteAsync(tokens.RequestToken!);
                    return;
                }
                await next(context);
            });
            app.UseAuthorization();
            app.UseMiddleware<UserFileUploadMiddleware>();
            app.UseEndpoints(endpoints => endpoints.MapAreaControllerRoute("user", "User", "User/{controller}/{action}"));
        })).StartAsync();

    /// <summary>Retains genuine antiforgery cookies and leaves rejected redirects observable.</summary>
    private static HttpClient Client(IHost host, bool authenticated)
    {
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
        if (authenticated) client.DefaultRequestHeaders.Add("X-Test-User", "true");
        return client;
    }
}
