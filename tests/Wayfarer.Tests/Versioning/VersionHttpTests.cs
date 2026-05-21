using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Middleware;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Versioning;

public class VersionHttpTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"wayfarer-version-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task GetVersion_ReturnsExpectedJsonAndContentType()
    {
        using var host = await CreateApiHostAsync();
        using var client = host.GetTestClient();

        using var response = await client.GetAsync("/api/version");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        using var document = JsonDocument.Parse(body);
        var property = document.RootElement.EnumerateObject().Should().ContainSingle().Subject;
        property.Name.Should().Be("version");
        property.Value.GetString().Should().Be("1.4.0");
    }

    [Fact]
    public async Task VersionHeader_AppearsOnApiResponse()
    {
        using var host = await CreateApiHostAsync();
        using var client = host.GetTestClient();

        using var response = await client.GetAsync("/api/version");

        response.Headers.GetValues(AppVersionHeaderMiddleware.HeaderName).Should().ContainSingle("1.4.0");
    }

    [Fact]
    public async Task VersionHeader_AppearsOnDocsStaticResponse()
    {
        Directory.CreateDirectory(_tempDirectory);
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "version-test.txt"), "docs");

        using var host = await CreateDocsStaticHostAsync(_tempDirectory);
        using var client = host.GetTestClient();

        using var response = await client.GetAsync("/docs/version-test.txt");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues(AppVersionHeaderMiddleware.HeaderName).Should().ContainSingle("1.4.0");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private static async Task<IHost> CreateApiHostAsync()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IAppVersionProvider>(new StubAppVersionProvider("1.4.0"));
                services.AddControllers()
                    .AddApplicationPart(typeof(VersionController).Assembly);
            })
            .Configure(app =>
            {
                app.UseMiddleware<AppVersionHeaderMiddleware>();
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            }))
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task<IHost> CreateDocsStaticHostAsync(string docsPath)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IAppVersionProvider>(new StubAppVersionProvider("1.4.0"));
            })
            .Configure(app =>
            {
                app.UseMiddleware<AppVersionHeaderMiddleware>();
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(docsPath),
                    RequestPath = "/docs",
                    ContentTypeProvider = new FileExtensionContentTypeProvider()
                });
            }))
            .Build();

        await host.StartAsync();
        return host;
    }

    private sealed class StubAppVersionProvider : IAppVersionProvider
    {
        public StubAppVersionProvider(string version)
        {
            Version = version;
        }

        public string Version { get; }
    }
}
