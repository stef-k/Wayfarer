using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WayfarerCtl;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Unique public transport boundaries, independent of Docker and published stable releases.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class PublicReleaseTests
{
    /// <summary>The common resolver selects exactly the supported native platform's asset, with no fallback.</summary>
    [Fact]
    public void PlatformSelectionNeverFallsBackToAnotherArchitecture()
    {
        var metadata = Metadata();
        var arm = Asset();
        arm["name"] = "wayfarer-v1.9.20-linux-arm64.tar.gz";
        metadata["assets"] = new[] { Asset(), arm };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata);
        Assert.Equal(arm["name"], PublicRelease.Parse(bytes, "latest", "linux/arm64").Asset);
        Assert.Equal(Asset()["name"], PublicRelease.Parse(bytes, "latest", "linux/amd64").Asset);
        metadata["assets"] = new[] { Asset() };
        Assert.Throws<UsageException>(() => PublicRelease.Parse(JsonSerializer.SerializeToUtf8Bytes(metadata), "latest", "linux/arm64"));
        Assert.Throws<IOException>(() => PublicRelease.Parse(bytes, "latest", "linux/arm/v7"));
    }

    /// <summary>Source-only releases, ambiguous assets and metadata redirects never grant acquisition authority.</summary>
    [Fact]
    public void MetadataRequiresExactStableIdentityAndRestDigest()
    {
        var valid = Metadata();
        var parsed = PublicRelease.Parse(JsonSerializer.SerializeToUtf8Bytes(valid), "1.9.20");
        Assert.Equal("v1.9.20", parsed.Tag);
        Assert.Equal(parsed, PublicRelease.Parse(JsonSerializer.SerializeToUtf8Bytes(valid), "latest"));
        foreach (var changed in new[] { "name", "tag_name", "html_url", "url" })
        {
            var invalid = Metadata();
            invalid[changed] = "https://evil.example/v1.9.20";
            Assert.ThrowsAny<Exception>(() => PublicRelease.Parse(JsonSerializer.SerializeToUtf8Bytes(invalid), "latest"));
        }
        valid["prerelease"] = true;
        Assert.Throws<UsageException>(() => PublicRelease.Parse(JsonSerializer.SerializeToUtf8Bytes(valid), "latest"));
        valid = Metadata();
        valid["assets"] = Array.Empty<object>();
        Assert.Throws<UsageException>(() => PublicRelease.Parse(JsonSerializer.SerializeToUtf8Bytes(valid), "latest"));
        var asset = Asset();
        valid["assets"] = new[] { asset, asset };
        Assert.Throws<UsageException>(() => PublicRelease.Parse(JsonSerializer.SerializeToUtf8Bytes(valid), "latest"));
        valid["assets"] = new[] { asset };
        asset["digest"] = "sha256:" + new string('A', 64);
        Assert.Throws<IOException>(() => PublicRelease.Parse(JsonSerializer.SerializeToUtf8Bytes(valid), "latest"));
        var text = JsonSerializer.Serialize(Metadata()).Insert(1, "\"draft\":true,");
        Assert.Throws<IOException>(() => PublicRelease.Parse(Encoding.UTF8.GetBytes(text), "latest"));
    }

    /// <summary>Exact bytes are hashed, size-bounded and required to match metadata before extraction.</summary>
    [Fact]
    public async Task DownloadRequiresExactDigestAndBoundedBody()
    {
        var bytes = Encoding.UTF8.GetBytes("bounded public archive bytes");
        var release = new PublicRelease("1.9.20", "v1.9.20", "wayfarer-v1.9.20-linux-amd64.tar.gz", bytes.Length,
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
        using var handler = new AssetResponse(bytes);
        using var client = new HttpClient(handler);
        var directory = Path.Combine(Path.GetTempPath(), "wayfarer-public-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var archive = Path.Combine(directory, "valid");
            await PublicReleaseAcquisition.DownloadAsync(client, release, archive, default);
            Assert.Equal(bytes, File.ReadAllBytes(archive));
            await Assert.ThrowsAsync<IOException>(() => PublicReleaseAcquisition.DownloadAsync(client,
                release with { Digest = "sha256:" + new string('0', 64) }, Path.Combine(directory, "bad-hash"), default));
            using var input = new MemoryStream(bytes);
            using var output = new MemoryStream();
            await Assert.ThrowsAsync<IOException>(() => PublicReleaseAcquisition.CopyAsync(input, output, bytes.Length - 1, default));
            Assert.Equal(0, output.Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>Only GitHub's fixed HTTPS asset CDN is authorized by the deterministic project download.</summary>
    [Theory]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/2?signature=opaque", true)]
    [InlineData("https://evil.example/github-production-release-asset/1/2", false)]
    [InlineData("http://release-assets.githubusercontent.com/github-production-release-asset/1/2", false)]
    [InlineData("https://user@release-assets.githubusercontent.com/github-production-release-asset/1/2", false)]
    public void RedirectTrustIsFixed(string value, bool expected) => Assert.Equal(expected, PublicReleaseAcquisition.AssetRedirect(new Uri(value)));

    /// <summary>The ordinary public planner still requires explicit --plan and accepts no alternative network selector.</summary>
    [Fact]
    public void PublicCommandsOnlyPrepareExistingUpdatePlans()
    {
        Assert.Equal("latest", UpdateOptions.Parse(["--plan"]).PublicVersion);
        Assert.Equal("1.9.20", UpdateOptions.Parse(["1.9.20", "--plan"]).PublicVersion);
        ReleaseCommands.Validate(["acquire", "latest"]);
        Assert.Throws<UsageException>(() => UpdateOptions.Parse(["latest", "--plan"]));
        Assert.Throws<UsageException>(() => UpdateOptions.Parse(["1.9.20"]));
        Assert.Throws<UsageException>(() => ReleaseCommands.Validate(["acquire", "https://evil.example"]));
    }

    /// <summary>Online setup accepts only exact versions and cannot mix local or injected release authority.</summary>
    [Fact]
    public void PublicSetupGrammarKeepsOneReleaseSelector()
    {
        Cli.ValidateCommand(["setup"]);
        Cli.ValidateCommand(["setup", "--version", "1.9.20"]);
        Cli.ValidateCommand(["setup", "--bundle", "/trusted/bundle"]);
        foreach (var options in new[]
        {
            "--version latest", "--version v1.9.20", "--version https://evil.example",
            "--version 1.9.20 --bundle /trusted/bundle", "--app-digest sha256:" + new string('a', 64),
            "--resume --version 1.9.20"
        })
            Assert.Throws<UsageException>(() => Setup.Options(options.Split(' ')));
    }

    private static Dictionary<string, object> Metadata() => new()
    {
        ["tag_name"] = "v1.9.20", ["name"] = "v1.9.20", ["draft"] = false, ["prerelease"] = false,
        ["html_url"] = "https://github.com/stef-k/Wayfarer/releases/tag/v1.9.20",
        ["url"] = "https://api.github.com/repos/stef-k/Wayfarer/releases/123", ["assets"] = new[] { Asset() }
    };

    private static Dictionary<string, object> Asset() => new()
    {
        ["name"] = "wayfarer-v1.9.20-linux-amd64.tar.gz", ["state"] = "uploaded", ["size"] = 100,
        ["digest"] = "sha256:" + new string('a', 64), ["browser_download_url"] = "https://evil.example/ignored"
    };

    /// <summary>A single controlled body exercises the product streaming seam without adding a server framework.</summary>
    private sealed class AssetResponse(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.StartsWith("https://github.com/stef-k/Wayfarer/releases/download/v1.9.20/", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
