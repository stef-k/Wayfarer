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
