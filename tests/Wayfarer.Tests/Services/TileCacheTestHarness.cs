using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wayfarer.Areas.Public.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Builds isolated tile-cache scopes around a recording in-memory upstream provider.
/// </summary>
internal sealed class TileCacheTestHarness : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan RefreshCleanupTimeout = TimeSpan.FromSeconds(2);
    private readonly ServiceProvider _rootProvider;
    private readonly TestTileSettingsService _settingsService;
    private int _disposed;

    /// <summary>Gets the isolated cache directory owned by this harness.</summary>
    public string CacheDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "wayfarer-tile-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Gets structured logs emitted by services created through the harness.</summary>
    public TestLogProvider Logs { get; } = new();

    /// <summary>Gets the fake upstream provider used by the harness.</summary>
    public RecordingTileHandler Upstream { get; }

    /// <summary>Gets the mutable settings snapshot supplied to tile services and controllers.</summary>
    public ApplicationSettings Settings => _settingsService.Settings;

    /// <summary>Creates an isolated harness with the supplied fake upstream behavior and host policy.</summary>
    public TileCacheTestHarness(
        RecordingTileHandler? upstream = null,
        string allowedHosts = "wayfarer.example.com")
    {
        Directory.CreateDirectory(CacheDirectory);
        Upstream = upstream ?? new RecordingTileHandler();
        _settingsService = new TestTileSettingsService();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CacheSettings:TileCacheDirectory"] = CacheDirectory,
                ["Application:ContactEmail"] = "tiles@example.test",
                ["AllowedHosts"] = allowedHosts
            })
            .Build();

        var services = new ServiceCollection();
        var databaseName = $"tile-diagnostics-{Guid.NewGuid():N}";
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IApplicationSettingsService>(_settingsService);
        services.AddScoped<IHttpContextAccessor, HttpContextAccessor>();
        services.AddSingleton(new HttpClient(Upstream) { Timeout = TimeSpan.FromSeconds(10) });
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(Logs);
        });
        services.AddSingleton<TileMetadataHotCache>();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        services.AddScoped<TileCacheService>();

        _rootProvider = services.BuildServiceProvider();
        TileCacheService.ResetStaticStateForTesting();
        TilesController.OutboundBudgetCache.Clear();
        TilesController.RateLimitCache.Clear();
        TilesController.AuthRateLimitCache.Clear();
    }

    /// <summary>Creates a fresh service scope for one independent tile request.</summary>
    public IServiceScope CreateScope() => _rootProvider.CreateScope();

    /// <summary>Creates a same-origin HTTP context for a tile request.</summary>
    public static DefaultHttpContext CreateHttpContext(CancellationToken cancellationToken = default)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("wayfarer.example.com");
        context.Request.Headers.Referer = "https://wayfarer.example.com/trip";
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        context.RequestAborted = cancellationToken;
        return context;
    }

    /// <summary>Cancels tracked refreshes before synchronously completing bounded test cleanup.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Awaits tracked refresh and scheduler completion before deleting temporary data.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var refreshesStopped =
            await TileCacheService.CancelAndWaitForRefreshesForTestingAsync(RefreshCleanupTimeout);
        if (!refreshesStopped)
        {
            Interlocked.Exchange(ref _disposed, 0);
            throw new TimeoutException("Tracked stale-tile refresh work exceeded the test cleanup timeout.");
        }

        await TileWorkScheduler.StopAndDrainAsync();
        TileCacheService.ResetStaticStateForTesting();
        await _rootProvider.DisposeAsync();

        if (Directory.Exists(CacheDirectory))
        {
            Directory.Delete(CacheDirectory, recursive: true);
        }
    }

    /// <summary>Supplies a mutable settings snapshot without persistence side effects.</summary>
    private sealed class TestTileSettingsService : IApplicationSettingsService
    {
        public ApplicationSettings Settings { get; } = new()
        {
            Id = 1,
            MaxCacheTileSizeInMB = 10,
            TileMetadataHotCacheSizeMB = ApplicationSettings.DefaultTileMetadataHotCacheSizeMB,
            TileProviderKey = ApplicationSettings.DefaultTileProviderKey,
            TileProviderUrlTemplate = ApplicationSettings.DefaultTileProviderUrlTemplate,
            TileRateLimitEnabled = false,
            TileOutboundBudgetPerIpPerMinute = 0
        };

        /// <inheritdoc />
        public ApplicationSettings GetSettings() => Settings;

        /// <inheritdoc />
        public string GetUploadsDirectoryPath() => Path.Combine(Path.GetTempPath(), "uploads");

        /// <inheritdoc />
        public void RefreshSettings() { }
    }
}

/// <summary>
/// Records every intercepted upstream request and returns deterministic local responses.
/// </summary>
internal sealed class RecordingTileHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responseFactory;
    private readonly Func<TimeSpan>? _startTimeProvider;
    private readonly ConcurrentQueue<RecordedTileRequest> _requests = new();

    /// <summary>Gets all intercepted requests in start order.</summary>
    public IReadOnlyCollection<RecordedTileRequest> Requests => _requests.ToArray();

    /// <summary>Creates a handler that returns cacheable PNG bytes.</summary>
    public RecordingTileHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? responseFactory = null,
        Func<TimeSpan>? startTimeProvider = null)
    {
        _startTimeProvider = startTimeProvider;
        _responseFactory = responseFactory ?? ((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4])
            };
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                MaxAge = TimeSpan.FromHours(1)
            };
            return Task.FromResult(response);
        });
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _requests.Enqueue(new RecordedTileRequest(
            request.RequestUri,
            _startTimeProvider?.Invoke() ?? TimeSpan.Zero,
            request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray())));
        return _responseFactory(request, cancellationToken);
    }
}

/// <summary>Captures the non-secret request data needed by provider-policy assertions.</summary>
internal sealed record RecordedTileRequest(
    Uri? RequestUri,
    TimeSpan StartTime,
    IReadOnlyDictionary<string, string[]> Headers);
