using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Wayfarer.ImagePrototype;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>Shared process slots require sequential local prototype checks.</summary>
[CollectionDefinition("Image helper prototype", DisableParallelization = true)]
public sealed class ImageHelperPrototypeCollection { }

/// <summary>Exercise the real executable through its sole standalone process owner.</summary>
[Collection("Image helper prototype")]
public sealed class ImageHelperPrototypeTests
{
    /// <summary>Web defaults apply only when both bounds are absent; PDF and fidelity recipes override them.</summary>
    [Theory]
    [InlineData(null, null, null, 2048, 2048, 90)]
    [InlineData(600, null, 85, 600, 8192, 85)]
    [InlineData(null, 400, null, 8192, 400, 90)]
    [InlineData(4096, null, 95, 4096, 8192, 95)]
    public void WebRecipe_ResolvesOnlyAbsentDefaults(int? width, int? height, int? quality,
        int expectedWidth, int expectedHeight, int expectedQuality)
    {
        var recipe = (width, height, quality) switch
        {
            (null, null, null) => new ImageTransform(),
            (int bound, null, int value) => new ImageTransform(Width: bound, Quality: value),
            (null, int bound, null) => new ImageTransform(Height: bound),
            _ => throw new ArgumentException("Unsupported focused profile case.")
        };
        Assert.Equal((expectedWidth, expectedHeight, expectedQuality),
            (recipe.Width, recipe.Height, recipe.Quality));
    }

    /// <summary>Opaque web resizing averages in sRGB; transparent resizing removes hidden color before filtering.</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Resize_PreservesSrgbAndPremultipliedAlphaContracts(bool transparent)
    {
        using var input = new Image<Rgba32>(128, 64);
        for (var y = 0; y < input.Height; y++)
            for (var x = 0; x < input.Width; x++)
                input[x, y] = transparent
                    ? (x % 2 == 0 ? new Rgba32(255, 0, 0, 255) : new Rgba32(0, 0, 255, 0))
                    : (x % 2 == 0 ? new Rgba32(255, 255, 255, 255) : new Rgba32(0, 0, 0, 255));
        using var encoded = new MemoryStream();
        input.SaveAsPng(encoded);
        var result = await Supervisor.TransformAsync(encoded.ToArray(),
            new ImageTransform(Width: 16, Height: 8), TimeSpan.FromSeconds(30));
        Assert.Equal(ImageTransformStatus.Success, result.Status);
        using var output = Image.Load<Rgba32>(result.Bytes!);
        var center = output[8, 4];
        Assert.Equal((16, 8), (output.Width, output.Height));
        Assert.InRange((int)center.R, transparent ? 254 : 126, transparent ? 255 : 130);
        Assert.InRange((int)center.G, transparent ? 0 : 126, transparent ? 1 : 130);
        Assert.InRange((int)center.B, transparent ? 0 : 126, transparent ? 1 : 130);
        Assert.InRange((int)center.A, transparent ? 126 : 255, transparent ? 130 : 255);
        AssertReaped(result.ProcessId);
    }

    /// <summary>The ordinary web profile keeps smaller real photographs at their original dimensions.</summary>
    [Fact]
    public async Task WebProfile_DoesNotUpscaleActualPhotograph()
    {
        var result = await TransformFixture("photo.jpg", new ImageTransform());
        Assert.Equal(ImageTransformStatus.Success, result.Status);
        using var output = Image.Load(result.Bytes!);
        Assert.Equal((1920, 863), (output.Width, output.Height));
        AssertReaped(result.ProcessId);
    }

    /// <summary>All accepted static formats produce independently decodable canonical output.</summary>
    [Theory]
    [InlineData("photo.jpg", "JPEG")]
    [InlineData("opaque.png", "PNG")]
    [InlineData("photo.webp", "JPEG")]
    [InlineData("photo.gif", "PNG")]
    public async Task SupportedFormats_TransformActualImages(string fixture, string format)
    {
        var result = await TransformFixture(fixture);
        Assert.Equal(ImageTransformStatus.Success, result.Status);
        using var image = Image.Load(result.Bytes!);
        Assert.Equal((600, 269), (image.Width, image.Height));
        Assert.Equal(format, image.Metadata.DecodedImageFormat!.Name);
        Assert.Single(image.Frames);
        Assert.NotNull(image.Metadata.IccProfile);
        Assert.Null(image.Metadata.ExifProfile);
        Assert.Null(image.Metadata.XmpProfile);
        AssertReaped(result.ProcessId);
    }

    /// <summary>All orientation geometries remain unchanged when the recipe would upscale.</summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public async Task Orientation_AppliesToJpegAndPngWithoutUpscale(int orientation)
    {
        foreach (var format in new[] { "jpg", "png" })
        {
            var result = await TransformFixture($"orientation-{orientation}.{format}");
            Assert.Equal(ImageTransformStatus.Success, result.Status);
            using var output = Image.Load(result.Bytes!);
            Assert.Equal(orientation < 5 ? (64, 32) : (32, 64), (output.Width, output.Height));
            Assert.Null(output.Metadata.ExifProfile);
            // The RGB8 fast path must preserve flips and rotations, not just swapped axes.
            if (format == "png")
            {
                using var expected = Image.Load<Rgba32>(File.ReadAllBytes(
                    Path.Combine(Fixtures, $"orientation-{orientation}.png")));
                expected.Mutate(context => context.AutoOrient());
                using var actual = Image.Load<Rgba32>(result.Bytes!);
                Assert.Equal(expected[expected.Width / 4, expected.Height / 4],
                    actual[actual.Width / 4, actual.Height / 4]);
                Assert.Equal(expected[3 * expected.Width / 4, 3 * expected.Height / 4],
                    actual[3 * actual.Width / 4, 3 * actual.Height / 4]);
            }
        }
    }

    /// <summary>Transparent pixels retain alpha and are never flattened by explicit JPEG routing.</summary>
    [Fact]
    public async Task Alpha_IsPreservedAndExplicitJpegRejects()
    {
        var result = await TransformFixture("alpha.png");
        using var output = Image.Load<Rgba32>(result.Bytes!);
        Assert.Contains(Enumerable.Range(0, output.Width * output.Height)
            .Select(index => output[index % output.Width, index / output.Width].A), alpha => alpha < 255);
        var failed = await TransformFixture("alpha.png", new ImageTransform(OutputFormat: "jpeg"));
        Assert.Equal(ImageTransformStatus.Failed, failed.Status);
        Assert.Null(failed.Bytes);
    }

    /// <summary>Retained malformed/animated controls run only under inherited kernel containment.</summary>
    [Theory]
    [InlineData("png-truncated-zlib.png")] [InlineData("png-invalid-filter.png")]
    [InlineData("png-bad-ihdr-crc.png")] [InlineData("truncated.png")]
    [InlineData("animated.png")] [InlineData("apng-lies-one-frame.png")]
    [InlineData("gif-truncated-lzw.gif")] [InlineData("gif-no-trailer.gif")]
    [InlineData("gif-truncated-descriptor.gif")] [InlineData("gif-truncated-extension.gif")]
    [InlineData("animated.gif")] [InlineData("animated.webp")] [InlineData("truncated.webp")]
    [InlineData("metadata.jpg")] [InlineData("metadata.png")]
    public async Task SourceFailures_DiscardAllOutput(string fixture)
    {
        var result = await TransformFixture(fixture);
        Assert.Contains(result.Status, new[] { ImageTransformStatus.Failed, ImageTransformStatus.TooLarge });
        Assert.Null(result.Bytes);
        AssertReaped(result.ProcessId);
    }

    /// <summary>Size evidence and output exhaustion have distinct classifications.</summary>
    [Fact]
    public async Task Limits_ClassifySourceAndOutputSeparately()
    {
        var tooLarge = await TransformFixture("over-width.png");
        Assert.Equal(ImageTransformStatus.TooLarge, tooLarge.Status);
        var unavailable = await TransformFixture("photo.jpg", new ImageTransform(MaximumOutputBytes: 100));
        Assert.Equal(ImageTransformStatus.Unavailable, unavailable.Status);
        Assert.Null(unavailable.Bytes);
        AssertReaped(unavailable.ProcessId);
    }

    /// <summary>Genuine ICC, CMYK/YCCK and gamma sources produce measured sRGB color controls.</summary>
    [Theory]
    [InlineData("p3.png", 225, 103, 58)]
    [InlineData("gray-icc.png", 129, 129, 129)]
    [InlineData("cmyk-tagged.jpg", 201, 151, 98)]
    [InlineData("cmyk-untagged.jpg", 199, 143, 69)]
    [InlineData("gamma-linear.png", 137, 137, 137)]
    [InlineData("gamma16-linear.png", 137, 137, 137)]
    [InlineData("cicp-srgb.png", 64, 128, 192)]
    public async Task Color_NormalizesSupportedDeclarations(string fixture, int red, int green, int blue)
    {
        var result = await TransformFixture(fixture);
        Assert.Equal(ImageTransformStatus.Success, result.Status);
        using var image = Image.Load<Rgba32>(result.Bytes!);
        var pixel = image[0, 0];
        Assert.InRange((int)pixel.R, red - 2, red + 2);
        Assert.InRange((int)pixel.G, green - 2, green + 2);
        Assert.InRange((int)pixel.B, blue - 2, blue + 2);
    }

    /// <summary>HDR declarations and full-raster GIFs without an LZW end code reject.</summary>
    [Theory]
    [InlineData("cicp-pq.png")]
    [InlineData("gif-full-raster-no-end.gif")]
    public async Task RequiredTerminationAndSdrPolicy_Rejects(string fixture)
    {
        var result = await TransformFixture(fixture);
        Assert.Equal(ImageTransformStatus.Failed, result.Status);
        Assert.Null(result.Bytes);
    }

    /// <summary>Repeated real decoding disposes child handles and permits subsequent launches.</summary>
    [Fact]
    public async Task RepeatedTransforms_ReapEveryChild()
    {
        for (var index = 0; index < 20; index++)
        {
            var result = await TransformFixture("static.jpg");
            Assert.Equal(ImageTransformStatus.Success, result.Status);
            AssertReaped(result.ProcessId);
        }
    }

    /// <summary>A kernel address-space refusal remains unavailable rather than source size evidence.</summary>
    [Fact]
    public async Task AddressSpaceLimit_IsEffective()
    {
        using var helper = new FaultHelper("""
            try:
                bytearray(2 * 1024 * 1024 * 1024)
            except MemoryError:
                sys.exit(70)
            sys.exit(0)
            """);
        var result = await helper.Supervisor.TransformAsync(ReadOnlyMemory<byte>.Empty,
            new ImageTransform(), TimeSpan.FromSeconds(30));
        Assert.Equal(70, result.ExitCode);
        Assert.Equal(ImageTransformStatus.Unavailable, result.Status);
        AssertReaped(result.ProcessId);
    }

    /// <summary>The kernel CPU limit fires before the independent 30-second wall deadline.</summary>
    [Fact]
    public async Task CpuLimit_IsEffective()
    {
        using var helper = new FaultHelper("while True: pass");
        var result = await helper.Supervisor.TransformAsync(ReadOnlyMemory<byte>.Empty,
            new ImageTransform(), TimeSpan.FromSeconds(30));
        Assert.Equal(128 + 24, result.ExitCode); // Linux SIGXCPU.
        Assert.Equal(ImageTransformStatus.Unavailable, result.Status);
        AssertReaped(result.ProcessId);
    }

    /// <summary>Process failures, including a stdout prefix, invalidate captured bytes.</summary>
    [Theory]
    [InlineData(2, ImageTransformStatus.Unavailable)]
    [InlineData(3, ImageTransformStatus.TooLarge)]
    [InlineData(4, ImageTransformStatus.Failed)]
    [InlineData(70, ImageTransformStatus.Unavailable)]
    [InlineData(99, ImageTransformStatus.Unavailable)]
    public async Task NonzeroExit_ClassifiesAndDiscardsPrefix(int exit, ImageTransformStatus expected)
    {
        using var helper = new FaultHelper($"os.write(1, b'prefix'); sys.exit({exit})");
        var result = await helper.Supervisor.TransformAsync(ReadOnlyMemory<byte>.Empty, new ImageTransform(), TimeSpan.FromSeconds(5));
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Bytes);
        Assert.Equal(exit, result.ExitCode);
        AssertReaped(result.ProcessId);
    }

    /// <summary>A crash and either output pipe overflow terminate and reap the owned child.</summary>
    [Theory]
    [InlineData("os.write(1, b'prefix'); os.kill(os.getpid(), signal.SIGKILL)")]
    [InlineData("while True: os.write(1, b'x'*8192)")]
    [InlineData("while True: os.write(2, b'x'*8192)")]
    public async Task CrashAndOverflow_AreUnavailable(string body)
    {
        using var helper = new FaultHelper(body);
        var result = await helper.Supervisor.TransformAsync(ReadOnlyMemory<byte>.Empty,
            new ImageTransform(MaximumOutputBytes: 1024), TimeSpan.FromSeconds(5));
        Assert.Equal(ImageTransformStatus.Unavailable, result.Status);
        Assert.Null(result.Bytes);
        AssertReaped(result.ProcessId);
    }

    /// <summary>Owned timeout and caller cancellation both complete only after reaping.</summary>
    [Fact]
    public async Task TimeoutAndCancellation_ReapBeforeCompletion()
    {
        using var helper = new FaultHelper("time.sleep(60)");
        var timedOut = await helper.Supervisor.TransformAsync(ReadOnlyMemory<byte>.Empty, new ImageTransform(), TimeSpan.FromMilliseconds(100));
        Assert.Equal(ImageTransformStatus.Unavailable, timedOut.Status);
        AssertReaped(timedOut.ProcessId);
        File.Delete(helper.PidPath);
        using var cancellation = new CancellationTokenSource();
        var pending = helper.Supervisor.TransformAsync(ReadOnlyMemory<byte>.Empty, new ImageTransform(), TimeSpan.FromSeconds(5), cancellation.Token);
        var pid = await helper.WaitForPid();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        AssertReaped(pid);
    }

    /// <summary>Four admitted children reject a fifth immediately and all slots recover after cleanup.</summary>
    [Fact]
    public async Task Admission_IsNonQueuingAndRecovers()
    {
        using var cancellation = new CancellationTokenSource();
        var helpers = Enumerable.Range(0, 4).Select(_ => new FaultHelper("time.sleep(60)")).ToArray();
        try
        {
            var tasks = helpers.Select(helper => helper.Supervisor.TransformAsync(ReadOnlyMemory<byte>.Empty,
                new ImageTransform(), TimeSpan.FromSeconds(5), cancellation.Token)).ToArray();
            var pids = await Task.WhenAll(helpers.Select(helper => helper.WaitForPid()));
            var fifth = await helpers[0].Supervisor.TransformAsync(ReadOnlyMemory<byte>.Empty, new ImageTransform(), TimeSpan.FromSeconds(5));
            Assert.Equal(ImageTransformStatus.Unavailable, fifth.Status);
            Assert.Null(fifth.ProcessId);
            cancellation.Cancel();
            foreach (var task in tasks)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            foreach (var pid in pids) AssertReaped(pid);
            Assert.Equal(ImageTransformStatus.Success, (await TransformFixture("static.jpg")).Status);
        }
        finally
        {
            cancellation.Cancel();
            foreach (var helper in helpers) helper.Dispose();
        }
    }

    /// <summary>The opt-in local fixtures are copied from retained evidence into this workspace.</summary>
    internal static string Fixtures => Environment.GetEnvironmentVariable("WAYFARER_IMAGE_FIXTURES")
        ?? throw new InvalidOperationException("Set WAYFARER_IMAGE_FIXTURES for this isolated prototype.");

    /// <summary>The real fixed executable is built locally with locked dependencies.</summary>
    internal static ImageHelperSupervisor Supervisor => new(
        Environment.GetEnvironmentVariable("WAYFARER_IMAGE_HELPER")
        ?? throw new InvalidOperationException("Set WAYFARER_IMAGE_HELPER for this isolated prototype."));

    /// <summary>Reuse retained image bytes without changing their source checkout.</summary>
    internal static Task<ImageTransformResult> TransformFixture(string name, ImageTransform? recipe = null) =>
        Supervisor.TransformAsync(File.ReadAllBytes(Path.Combine(Fixtures, name)),
            recipe ?? new ImageTransform(Width: 600, Height: 8192, Quality: 85), TimeSpan.FromSeconds(30));

    /// <summary>A completed operation must leave no live child or unreaped zombie.</summary>
    internal static void AssertReaped(int? pid)
    {
        Assert.NotNull(pid);
        Assert.False(Directory.Exists($"/proc/{pid}"));
    }
}
