using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Middleware;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Versioning;

public class VersionHttpTests
{
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
        property.Value.GetString().Should().Be("1.4.1");
    }

    [Fact]
    public async Task VersionHeader_AppearsOnApiResponse()
    {
        using var host = await CreateApiHostAsync();
        using var client = host.GetTestClient();

        using var response = await client.GetAsync("/api/version");

        response.Headers.GetValues(AppVersionHeaderMiddleware.HeaderName).Should().ContainSingle("1.4.1");
    }

    private static async Task<IHost> CreateApiHostAsync()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IAppVersionProvider>(new StubAppVersionProvider("1.4.1"));
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

    private sealed class StubAppVersionProvider : IAppVersionProvider
    {
        public StubAppVersionProvider(string version)
        {
            Version = version;
        }

        public string Version { get; }
    }
}
