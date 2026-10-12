using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
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
    /// <summary>Cold runs use a fresh test host; warm runs alternate backend order after three warmups.</summary>
    [ImageHelperPerformanceFact]
    public async Task RecordMatchedProfile()
    {
        var fixture = Environment.GetEnvironmentVariable("WAYFARER_IMAGE_PERF_INPUT")!;
        var report = Environment.GetEnvironmentVariable("WAYFARER_IMAGE_PERF_REPORT")
            ?? throw new InvalidOperationException("Set the local performance report path.");
        var phase = Environment.GetEnvironmentVariable("WAYFARER_IMAGE_PERF_PHASE") ?? "warm";
        var repetitions = int.Parse(Environment.GetEnvironmentVariable("WAYFARER_IMAGE_PERF_REPETITIONS") ?? "20");
        var input = File.ReadAllBytes(Path.Combine(ImageHelperPrototypeTests.Fixtures, fixture));
        var supervisor = ImageHelperPrototypeTests.Supervisor;
        var observations = new List<object>();
        for (var iteration = phase == "warm" ? -3 : 0; iteration < repetitions; iteration++)
        {
            var backends = iteration % 2 == 0 ? new[] { "imagesharp", "rust" } : new[] { "rust", "imagesharp" };
            foreach (var backend in backends)
            {
                byte[] output;
                var timer = Stopwatch.StartNew();
                if (backend == "imagesharp")
                {
                    output = ImageProxyHelper.OptimizeImage(input, 600, 8192, 85, out _);
                    ImageProxyHelper.ValidateRaster(output);
                }
                else
                {
                    var result = await supervisor.TransformAsync(input, new ImageTransform(), TimeSpan.FromSeconds(30));
                    if (result.Status != ImageTransformStatus.Success)
                        throw new InvalidOperationException("The measured transform did not succeed.");
                    output = result.Bytes!;
                }
                timer.Stop();
                // Geometry/metadata inspection and evidence file writes occur after the timed operation.
                using var decoded = SixLabors.ImageSharp.Image.Load(output);
                if ((decoded.Width, decoded.Height) != (600, 269))
                    throw new InvalidOperationException("The compared profile geometry differs.");
                observations.Add(new { backend, fixture, phase, iteration, milliseconds = timer.Elapsed.TotalMilliseconds,
                    bytes = output.Length, width = decoded.Width, height = decoded.Height,
                    sha256 = Convert.ToHexString(SHA256.HashData(output)) });
                if (iteration == 0)
                    File.WriteAllBytes(report + "." + backend + (decoded.Metadata.DecodedImageFormat!.Name == "PNG" ? ".png" : ".jpg"), output);
            }
        }
        File.WriteAllText(report, JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }));
    }
}
