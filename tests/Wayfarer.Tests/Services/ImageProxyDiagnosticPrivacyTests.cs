using Wayfarer.Parsers;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Qualifies explicit and factory image-origin diagnostics with credential-bearing URLs.</summary>
[Collection(ImageProxyStaticStateTestCollection.Name)]
public sealed class ImageProxyDiagnosticPrivacyTests
{
    private const string SecretUrl = "https://example.com/private-path-663.jpg?key=private-query-663";

    /// <summary>Exercises the actual typed factory pipeline with both its default and suppressed loggers.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedClient_QualifiesFactoryLogging(bool suppress)
    {
        using var logs = new TestLogProvider();
        var services = new ServiceCollection().AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton(Mock.Of<IProxiedImageCacheService>());
        services.AddSingleton(Mock.Of<IApplicationSettingsService>(s => s.GetSettings() == new ApplicationSettings()));
        var client = services.AddHttpClient<IImageProxyService, ImageProxyService>()
            .ConfigurePrimaryHttpMessageHandler(() => new FailingOrigin());
        if (suppress) client.RemoveAllLoggers();
        await using var provider = services.BuildServiceProvider();
        var proxy = provider.GetRequiredService<IImageProxyService>();
        var result = await proxy.RefreshAsync(new ImageProxyRequest(SecretUrl, Optimize: false));
        Assert.Equal(ImageProxyResultStatus.Failed, result.Status);
        var factoryLogs = logs.Entries.Where(e => e.Category.StartsWith("System.Net.Http.HttpClient")).ToArray();
        if (!suppress)
        {
            // Default factory logging exposes at least the private origin path, even if queries are redacted.
            Assert.Contains(factoryLogs, e => e.Message.Contains("private-path-663"));
        }
        else
        {
            Assert.Empty(factoryLogs);
            Assert.NotEmpty(logs.Entries);
            Assert.All(logs.Entries, entry =>
            {
                Assert.Null(entry.Exception);
                var text = entry.Message + string.Join(",", entry.Fields.Values);
                Assert.DoesNotContain("private-path-663", text);
                Assert.DoesNotContain("private-query-663", text);
                Assert.DoesNotContain("origin-exception-663", text);
            });
            Assert.Contains(logs.Entries, entry => entry.Fields.ContainsKey("CacheKey"));
        }
    }

    /// <summary>Simulates a transport whose exception repeats the full origin URL.</summary>
    private sealed class FailingOrigin : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException($"origin-exception-663 {request.RequestUri}");
    }
}
