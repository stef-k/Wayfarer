using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Wayfarer.Middleware;
using Xunit;

namespace Wayfarer.Tests.Middleware;

/// <summary>
/// Verifies API error responses preserve terminal pipeline status codes after JSON conversion.
/// </summary>
public class ApiErrorResponseMiddlewareTests
{
    [Theory]
    [InlineData(401, "Unauthorized")]
    [InlineData(403, "Forbidden")]
    [InlineData(404, "Not Found")]
    public async Task ApiTerminalError_PreservesStatusAndReturnsJson(int statusCode, string error)
    {
        using var host = await CreateHostAsync(statusCode);
        using var response = await host.GetTestClient().GetAsync("/api/test-error");

        response.StatusCode.Should().Be((HttpStatusCode)statusCode);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("status").GetInt32().Should().Be(statusCode);
        document.RootElement.GetProperty("error").GetString().Should().Be(error);
    }

    private static async Task<IHost> CreateHostAsync(int statusCode)
    {
        var host = new HostBuilder().ConfigureWebHost(webHost => webHost.UseTestServer().Configure(app =>
        {
            app.UseMiddleware<ApiErrorResponseMiddleware>();
            app.Run(context => { context.Response.StatusCode = statusCode; return Task.CompletedTask; });
        })).Build();
        await host.StartAsync();
        return host;
    }
}
