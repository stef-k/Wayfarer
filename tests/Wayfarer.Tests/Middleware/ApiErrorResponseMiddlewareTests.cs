using Microsoft.Extensions.Logging;
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

    /// <summary>Unhandled failures keep the envelope and correlate only bounded diagnostic context.</summary>
    [Fact]
    public async Task UnhandledFailure_OmitsExceptionAndUsesExistingRequestId()
    {
        using var logs = new Wayfarer.Tests.Infrastructure.TestLogProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Path = "/api/test-error";
        context.TraceIdentifier = "request-663";
        context.Response.Body = new MemoryStream();
        var middleware = new ApiErrorResponseMiddleware(_ => throw new InvalidOperationException("secret-663"));
        await middleware.InvokeAsync(context, factory.CreateLogger<ApiErrorResponseMiddleware>());
        context.Response.Body.Position = 0;
        var json = await new StreamReader(context.Response.Body).ReadToEndAsync();
        using var document = JsonDocument.Parse(json);
        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal("Internal Server Error", document.RootElement.GetProperty("error").GetString());
        Assert.Equal("An unexpected error occurred.", document.RootElement.GetProperty("message").GetString());
        Assert.Equal("The request could not be completed.", document.RootElement.GetProperty("details").GetString());
        Assert.Equal("request-663", document.RootElement.GetProperty("requestId").GetString());
        Assert.DoesNotContain("secret-663", json);
        var entry = Assert.Single(logs.Entries);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain("secret-663", entry.Message + string.Join(",", entry.Fields.Values));
        Assert.Equal("InvalidOperationException", entry.Fields["ExceptionType"]);
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
