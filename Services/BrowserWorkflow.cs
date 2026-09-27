using System.Runtime.InteropServices;
using Microsoft.Playwright;

namespace Wayfarer.Services;

/// <summary>Nonqueued process-wide admission, independent of request/service lifetimes.</summary>
internal sealed class BrowserAdmission
{
    internal static BrowserAdmission Shared { get; } = new();
    private int _active;

    /// <summary>Immediately claims one of two slots; no waiting tasks are retained.</summary>
    internal IDisposable? TryAcquire()
    {
        while (true)
        {
            var active = Volatile.Read(ref _active);
            if (active >= 2) return null;
            if (Interlocked.CompareExchange(ref _active, active + 1, active) == active)
                return new Lease(this);
        }
    }

    /// <summary>Releases its slot exactly once, even under concurrent disposal.</summary>
    private sealed class Lease(BrowserAdmission owner) : IDisposable
    {
        private BrowserAdmission? _owner = owner;
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner != null) Interlocked.Decrement(ref owner._active);
        }
    }
}

/// <summary>Signals bounded unavailability without exposing browser or destination details.</summary>
public sealed class BrowserUnavailableException(string message) : Exception(message);

/// <summary>Owns one Chromium process for one admitted workflow, with active cancellation and bounded cleanup.</summary>
internal sealed class BrowserWorkflow : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly CancellationTokenRegistration _cancellation;
    private readonly object _closeLock = new();
    private Task? _closeTask;
    internal BrowserCapturePolicy Policy { get; }

    private BrowserWorkflow(IPlaywright playwright, IBrowser browser, BrowserCapturePolicy policy, CancellationToken token)
    {
        _playwright = playwright;
        _browser = browser;
        Policy = policy;
        _cancellation = token.Register(() => { _ = CloseAsync(); });
    }

    /// <summary>Launches once; startup failures dispose the driver and never retain a browser.</summary>
    internal static async Task<BrowserWorkflow> StartAsync(BrowserCapturePolicy policy, CancellationToken token,
        Func<Task<IPlaywright>>? factory = null)
    {
        token.ThrowIfCancellationRequested();
        var playwright = await (factory?.Invoke() ?? Playwright.CreateAsync());
        try
        {
            token.ThrowIfCancellationRequested();
            var browser = await BrowserRuntime.LaunchAsync(playwright, new BrowserTypeLaunchOptions
            {
                Headless = true, Args = LaunchArguments(policy), Timeout = 30000
            });
            return new BrowserWorkflow(playwright, browser, policy, token);
        }
        catch { playwright.Dispose(); throw; }
    }

    /// <summary>Keeps only loopback mapping and the existing native ARM64 compatibility arguments.</summary>
    internal static string[] LaunchArguments(BrowserCapturePolicy policy)
    {
        var args = new List<string> { $"--host-resolver-rules={policy.HostResolverRule}" };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.OSArchitecture == Architecture.Arm64)
            args.AddRange(["--no-sandbox", "--disable-dev-shm-usage", "--disable-gpu"]);
        return args.ToArray();
    }

    /// <summary>Every isolated capture context receives the same policy before cookies or pages.</summary>
    internal async Task<IBrowserContext> NewContextAsync(int width, int height, IEnumerable<Cookie>? cookies = null)
    {
        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = height },
            ServiceWorkers = ServiceWorkerPolicy.Block, AcceptDownloads = false
        });
        try
        {
            await Policy.ConfigureAsync(context);
            if (cookies != null) await context.AddCookiesAsync(cookies);
            return context;
        }
        catch { await context.CloseAsync(); throw; }
    }

    /// <summary>Cancellation and ordinary disposal share one bounded close operation.</summary>
    private Task CloseAsync()
    {
        lock (_closeLock) return _closeTask ??= CloseCoreAsync();
    }

    private async Task CloseCoreAsync()
    {
        try { await _browser.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception) { /* Driver disposal below is the final cleanup; preserve the workflow's primary failure. */ }
        finally { _playwright.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.DisposeAsync();
        await CloseAsync();
    }
}
