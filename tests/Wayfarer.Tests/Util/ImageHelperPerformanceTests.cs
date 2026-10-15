using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using Wayfarer.ImagePrototype;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>Explicit local measurements; ordinary test runs skip this observation-only case.</summary>
public sealed class ImageHelperPerformanceFactAttribute : FactAttribute
{
    /// <summary>Performance is opt-in and has no timing assertion or CI gate.</summary>
    public ImageHelperPerformanceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WAYFARER_IMAGE_PERF_INPUT") is null)
            Skip = "Local #858 performance observation requires an explicit input fixture.";
    }
}

/// <summary>Reuse the unchanged production ImageSharp seam for one matched output recipe.</summary>
[Collection("Image helper prototype")]
public sealed class ImageHelperPerformanceTests
{
    /// <summary>Reuse the retained paired collector with explicit web/PDF recipes and optional original/4:4:4 controls.</summary>
    [ImageHelperPerformanceFact]
    public async Task RecordMatchedProfile()
    {
        var fixture = Environment.GetEnvironmentVariable("WAYFARER_IMAGE_PERF_INPUT")!;
        var report = Environment.GetEnvironmentVariable("WAYFARER_IMAGE_PERF_REPORT")
            ?? throw new InvalidOperationException("Set the local performance report path.");
        var phase = Environment.GetEnvironmentVariable("WAYFARER_IMAGE_PERF_PHASE") ?? "warm";
        var repetitions = int.Parse(Environment.GetEnvironmentVariable("WAYFARER_IMAGE_PERF_REPETITIONS") ?? "20");
        var warmups = int.Parse(Environment.GetEnvironmentVariable("WAYFARER_IMAGE_PERF_WARMUPS") ?? "20");
        var width = int.Parse(Environment.GetEnvironmentVariable("WAYFARER_IMAGE_PERF_WIDTH") ?? "2048");
        var height = int.Parse(Environment.GetEnvironmentVariable("WAYFARER_IMAGE_PERF_HEIGHT") ?? "2048");
        var quality = int.Parse(Environment.GetEnvironmentVariable("WAYFARER_IMAGE_PERF_QUALITY") ?? "90");
        var orderOffset = int.Parse(Environment.GetEnvironmentVariable("WAYFARER_IMAGE_PERF_ORDER") ?? "0");
        var recipe = new ImageTransform(Width: width, Height: height, Quality: quality);
        var input = File.ReadAllBytes(Path.Combine(ImageHelperPrototypeTests.Fixtures, fixture));
        var info = Image.Identify(input);
        var scale = Math.Min(1, Math.Min((double)width / info.Width, (double)height / info.Height));
        var expected = (Math.Max(1, (int)(info.Width * scale)), Math.Max(1, (int)(info.Height * scale)));
        var supervisor = ImageHelperPrototypeTests.Supervisor;
        var originalPath = Environment.GetEnvironmentVariable("WAYFARER_IMAGE_PERF_ORIGINAL");
        var original = originalPath is null ? null : new ImageHelperSupervisor(originalPath);
        var selectedBackends = new List<string> { "imagesharp", "rust" };
        if (original is not null) selectedBackends.Add("original");
        if (Environment.GetEnvironmentVariable("WAYFARER_IMAGE_PERF_JPEG444") == "1")
            selectedBackends.Add("imagesharp444");
        var observations = new List<object>();
        using var host = Process.GetCurrentProcess();
        for (var iteration = phase == "warm" ? -warmups : 0; iteration < repetitions; iteration++)
        {
            var backends = (iteration + orderOffset) % 2 == 0
                ? selectedBackends.ToArray() : selectedBackends.AsEnumerable().Reverse().ToArray();
            foreach (var backend in backends)
            {
                byte[] output;
                var cpu = host.TotalProcessorTime;
                var timer = Stopwatch.StartNew();
                if (backend == "imagesharp")
                {
                    output = ImageProxyHelper.OptimizeImage(input, width, height, quality, out _);
                    ImageProxyHelper.ValidateRaster(output);
                }
                else if (backend == "imagesharp444")
                    output = MatchedJpeg444(input, expected, quality);
                else
                {
                    var owner = backend == "original" ? original! : supervisor;
                    var result = await owner.TransformAsync(input, recipe, TimeSpan.FromSeconds(30));
                    if (result.Status != ImageTransformStatus.Success)
                        throw new InvalidOperationException("The measured transform did not succeed.");
                    output = result.Bytes!;
                }
                timer.Stop();
                var parentCpuMilliseconds = (host.TotalProcessorTime - cpu).TotalMilliseconds;
                host.Refresh();
                // Host RSS is a shared cumulative observation, not an attributable child/operation peak.
                var hostRssKiB = host.WorkingSet64 / 1024;
                var hostPeakRssKiB = host.PeakWorkingSet64 / 1024;
                // Geometry/metadata inspection and evidence file writes occur after the timed operation.
                using var decoded = SixLabors.ImageSharp.Image.Load(output);
                if ((decoded.Width, decoded.Height) != expected)
                    throw new InvalidOperationException("The compared profile geometry differs.");
                observations.Add(new { backend, fixture, phase, iteration, milliseconds = timer.Elapsed.TotalMilliseconds,
                    widthLimit = width, heightLimit = height, quality, parentCpuMilliseconds, hostRssKiB, hostPeakRssKiB,
                    bytes = output.Length, width = decoded.Width, height = decoded.Height,
                    sha256 = Convert.ToHexString(SHA256.HashData(output)) });
                if (iteration == 0)
                    File.WriteAllBytes(report + "." + backend + (decoded.Metadata.DecodedImageFormat!.Name == "PNG" ? ".png" : ".jpg"), output);
            }
        }
        File.WriteAllText(report, JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Observe finite standalone loads inside an operator-created cgroup; ordinary CI skips this case.</summary>
    [ImageHelperPerformanceFact]
    public async Task RecordStandaloneLoad()
    {
        var report = Environment.GetEnvironmentVariable("WAYFARER_IMAGE_PERF_REPORT")
            ?? throw new InvalidOperationException("Set the local observation report path.");
        var mode = Environment.GetEnvironmentVariable("WAYFARER_IMAGE_LOAD_MODE") ?? "batch";
        var count = int.Parse(Environment.GetEnvironmentVariable("WAYFARER_IMAGE_LOAD_COUNT") ?? "1");
        if (count is not (1 or 4 or 10 or 20) || mode is not ("batch" or "burst" or "cancel"))
            throw new InvalidOperationException("Only the fixed readiness workloads are supported.");
        var group = File.ReadAllText("/proc/self/cgroup").Trim().Split(':')[2];
        var cgroup = "/sys/fs/cgroup" + group;
        if (File.ReadAllText(Path.Combine(cgroup, "memory.max")).Trim() != "3221225472")
            throw new InvalidOperationException("Readiness loads require the finite 3 GiB cgroup.");
        var names = new[] { "large-vilaplana.jpg", "large-vilaplana.webp", "alpha-large.png", "photo.gif" };
        var inputs = names.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(ImageHelperPrototypeTests.Fixtures, name)));
        var observations = new List<LoadObservation>();
        var observedHelpers = new HashSet<int>();
        long peakRss = 0;
        var maximumChildren = 0;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                var sample = SampleCgroup(cgroup);
                peakRss = Math.Max(peakRss, sample.Rss);
                maximumChildren = Math.Max(maximumChildren, sample.Helpers.Length);
                foreach (var pid in sample.Helpers) observedHelpers.Add(pid);
                await Task.Delay(2);
            }
        });
        using var caller = new CancellationTokenSource();
        using var parent = Process.GetCurrentProcess();
        var parentCpu = parent.TotalProcessorTime;
        var cpuBefore = CgroupCpuMicroseconds(cgroup);
        var timer = Stopwatch.StartNew();
        if (mode == "cancel") caller.CancelAfter(TimeSpan.FromMilliseconds(100));

        async Task Observe(int index)
        {
            var name = mode == "cancel" ? "alpha-large.png" : names[index % names.Length];
            LoadObservation observation;
            try
            {
                var result = await ImageHelperPrototypeTests.Supervisor.TransformAsync(inputs[name],
                    new ImageTransform(), TimeSpan.FromSeconds(30), caller.Token);
                observation = new(index, name, result.Status.ToString(), result.Bytes?.Length ?? 0,
                    result.ProcessId, timer.Elapsed.TotalMilliseconds,
                    result.ProcessId is null || !Directory.Exists($"/proc/{result.ProcessId}"));
            }
            catch (OperationCanceledException) when (caller.IsCancellationRequested)
            {
                observation = new(index, name, "Canceled", 0, null, timer.Elapsed.TotalMilliseconds, true);
            }
            lock (observations) observations.Add(observation);
        }

        try
        {
            var pending = new List<Task>();
            for (var index = 0; index < count; index++)
            {
                // This finite feeder owns its next input; the supervisor still never queues admission.
                if (mode == "batch" && pending.Count == 4)
                {
                    var completed = await Task.WhenAny(pending);
                    pending.Remove(completed);
                    await completed;
                }
                pending.Add(Observe(index));
            }
            await Task.WhenAll(pending);
        }
        finally
        {
            timer.Stop();
            sampling.Cancel();
            await sampler;
        }
        var cpuMicroseconds = CgroupCpuMicroseconds(cgroup) - cpuBefore;
        var parentCpuMilliseconds = (parent.TotalProcessorTime - parentCpu).TotalMilliseconds;
        var helpersReaped = observedHelpers.All(pid => !Directory.Exists($"/proc/{pid}"));
        var recovery = mode == "cancel"
            ? (await ImageHelperPrototypeTests.TransformFixture("static.jpg")).Status.ToString() : null;
        var successes = observations.Where(row => row.Status == "Success").ToArray();
        File.WriteAllText(report, JsonSerializer.Serialize(new {
            mode, count, observations, firstResponseMilliseconds = observations.Min(row => row.CompletedMilliseconds),
            firstSuccessMilliseconds = successes.Length == 0 ? (double?)null : successes.Min(row => row.CompletedMilliseconds),
            totalMilliseconds = timer.Elapsed.TotalMilliseconds, success = successes.Length,
            rejected = observations.Count(row => row.Status == "Unavailable" && row.ProcessId is null),
            canceled = observations.Count(row => row.Status == "Canceled"), peakSampledCgroupProcessRssBytes = peakRss,
            kernelCgroupPeakBytes = long.Parse(File.ReadAllText(Path.Combine(cgroup, "memory.peak"))),
            cgroupCpuMilliseconds = cpuMicroseconds / 1000.0, parentCpuMilliseconds, maximumChildren,
            observedHelpers = observedHelpers.Order().ToArray(), helpersReaped, recovery,
            memoryEvents = File.ReadAllText(Path.Combine(cgroup, "memory.events")),
            cpuStat = File.ReadAllText(Path.Combine(cgroup, "cpu.stat"))
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>One completed request observation; no derivative is retained by the batch feeder.</summary>
    private sealed record LoadObservation(int Index, string Fixture, string Status, int Bytes,
        int? ProcessId, double CompletedMilliseconds, bool Reaped);

    /// <summary>Kernel CPU accounting includes already reaped children in this finite cgroup.</summary>
    private static long CgroupCpuMicroseconds(string cgroup) => long.Parse(File.ReadAllLines(
        Path.Combine(cgroup, "cpu.stat")).Single(line => line.StartsWith("usage_usec ")).Split(' ')[1]);

    /// <summary>Sample simultaneous RSS across the managed host, launchers and helpers, allowing process exit races.</summary>
    private static (long Rss, int[] Helpers) SampleCgroup(string cgroup)
    {
        long rss = 0;
        var helpers = new List<int>();
        foreach (var line in File.ReadAllLines(Path.Combine(cgroup, "cgroup.procs")))
        {
            try
            {
                using var process = Process.GetProcessById(int.Parse(line));
                rss += process.WorkingSet64;
                if (process.ProcessName == "wayfarer-image") helpers.Add(process.Id);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException
                or System.ComponentModel.Win32Exception) { }
        }
        return (rss, helpers.ToArray());
    }

    /// <summary>Supplemental supported-API control matches Rust's fixed 4:4:4 encoder and conventional Lanczos3.
    /// The unchanged production 4:2:0 seam remains the primary gate; this control cannot replace it.</summary>
    private static byte[] MatchedJpeg444(byte[] input, (int Width, int Height) geometry, int quality)
    {
        var preflight = ImageProxyHelper.PreflightDecodedResources(input);
        if (preflight.Decision != DecodedImageResourceDecision.Accepted)
            throw new InvalidOperationException("The matched source failed resource admission.");
        using var image = DecodedImageResourceLimits.Load(input.AsSpan());
        if ((image.Width, image.Height) != geometry)
            image.Mutate(context => context.Resize(geometry.Width, geometry.Height, KnownResamplers.Lanczos3));
        using var output = new MemoryStream();
        image.SaveAsJpeg(output, new JpegEncoder { Quality = quality, ColorType = JpegEncodingColor.YCbCrRatio444 });
        var bytes = output.ToArray();
        ImageProxyHelper.ValidateRaster(bytes);
        return bytes;
    }
}
