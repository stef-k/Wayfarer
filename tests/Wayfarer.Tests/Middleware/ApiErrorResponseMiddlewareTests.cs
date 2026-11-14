using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
        response.Headers.GetValues("Set-Cookie").Should().Contain("session=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/");
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
        context.Response.Headers.SetCookie = "session=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/";
        var middleware = new ApiErrorResponseMiddleware(_ => throw new InvalidOperationException("secret-663"));
        await middleware.InvokeAsync(context, factory.CreateLogger<ApiErrorResponseMiddleware>());
        context.Response.Body.Position = 0;
        var json = await new StreamReader(context.Response.Body).ReadToEndAsync();
        using var document = JsonDocument.Parse(json);
        Assert.Equal(500, context.Response.StatusCode);
        Assert.Contains("session=;", context.Response.Headers.SetCookie.ToString());
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

    /// <summary>Navigation aborts propagate to the host without a synthetic error response or error log.</summary>
    [Fact]
    public async Task AbortedRequestCancellation_DoesNotBecomeServerFailure()
    {
        using var logs = new Wayfarer.Tests.Infrastructure.TestLogProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        context.Request.Path = "/api/trip-compositions/candidates";
        context.Response.Body = new MemoryStream();
        var failure = new OperationCanceledException(cancellation.Token);
        var middleware = new ApiErrorResponseMiddleware(_ => throw failure);
        Assert.Same(failure, await Assert.ThrowsAsync<OperationCanceledException>(() =>
            middleware.InvokeAsync(context, factory.CreateLogger<ApiErrorResponseMiddleware>())));
        Assert.Empty(logs.Entries);
        Assert.Equal(0, context.Response.Body.Length);
        Assert.NotEqual(500, context.Response.StatusCode);
    }

    /// <summary>An internal cancellation or an unrelated failure during an abort remains a server error.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServerFailure_StillGetsErrorEnvelope(bool requestAborted)
    {
        using var logs = new Wayfarer.Tests.Infrastructure.TestLogProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/test-error";
        context.Response.Body = new MemoryStream();
        Exception failure = requestAborted ? new InvalidOperationException() : new OperationCanceledException();
        if (requestAborted) context.RequestAborted = new CancellationToken(true);
        await new ApiErrorResponseMiddleware(_ => throw failure)
            .InvokeAsync(context, factory.CreateLogger<ApiErrorResponseMiddleware>());
        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal(failure.GetType().Name, Assert.Single(logs.Entries).Fields["ExceptionType"]);
    }

    /// <summary>The managed-import adapter retains ownership of all its exceptions, including aborts.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedImportFailure_PropagatesToItsAdapter(bool aborted)
    {
        using var logs = new Wayfarer.Tests.Infrastructure.TestLogProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var context = new DefaultHttpContext { RequestAborted = new CancellationToken(aborted) };
        context.Request.Path = "/api/import";
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new ManagedImportAttribute()), "managed"));
        Exception failure = aborted ? new OperationCanceledException(context.RequestAborted) : new IOException();
        var middleware = new ApiErrorResponseMiddleware(_ => throw failure);
        Assert.Same(failure, await Record.ExceptionAsync(() =>
            middleware.InvokeAsync(context, factory.CreateLogger<ApiErrorResponseMiddleware>())));
        Assert.Empty(logs.Entries);
        Assert.Equal(0, context.Response.Body.Length);
    }

    private static async Task<IHost> CreateHostAsync(int statusCode)
    {
        var host = new HostBuilder().ConfigureWebHost(webHost => webHost.UseTestServer().Configure(app =>
        {
            app.UseMiddleware<ApiErrorResponseMiddleware>();
            app.Run(context =>
            {
                // Terminal no-body results must retain cookie deletions when normalization clears the response.
                context.Response.Headers.SetCookie = "session=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/";
                context.Response.StatusCode = statusCode;
                return Task.CompletedTask;
            });
        })).Build();
        await host.StartAsync();
        return host;
    }
}
