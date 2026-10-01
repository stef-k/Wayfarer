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
    /// <summary>Transport details must become a safe provider failure before any installation is created.</summary>
    [Fact]
    public async Task MetadataTransportFailureHasSafeProviderDiagnostic()
    {
        using var handler = new ResponseSequence(_ => throw new HttpRequestException("private-token-and-url"));
        using var client = new HttpClient(handler);
        var delays = new List<TimeSpan>();
        var progress = new List<string>();
        var error = await Assert.ThrowsAsync<AcquisitionException>(() => PublicReleaseAcquisition.ResolveAsync(client, "latest", default,
            progress.Add, (duration, _) => { delays.Add(duration); return Task.CompletedTask; }));
        Assert.Equal(4, handler.Calls);
        Assert.Equal(new[] { 2, 5, 10 }, delays.Select(value => (int)value.TotalSeconds));
        Assert.Contains("Retrying (4/4)", string.Join('\n', progress));
        var terminal = new FailureTerminal();
        Assert.Equal(1, new Setup(new FailureProcess(), terminal).ReportFailure(error, false));
        Assert.Contains("GitHub", terminal.Errors);
        Assert.Contains("Setup has not started", terminal.Errors);
        Assert.Contains("same 'wayfarerctl setup' command", terminal.Errors);
        Assert.DoesNotContain("--resume", terminal.Errors);
        Assert.DoesNotContain("doctor", terminal.Errors);
        Assert.DoesNotContain("private-token-and-url", terminal.Errors);
    }

    /// <summary>A temporary server failure converges through the same resolver, with a capped Retry-After.</summary>
    [Fact]
    public async Task MetadataRetryHonorsOnlyBoundedServerDelay()
    {
        using var handler = new ResponseSequence(attempt =>
        {
            if (attempt > 1) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(Metadata())) };
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new(TimeSpan.FromMinutes(5));
            return response;
        });
        using var client = new HttpClient(handler);
        var delays = new List<TimeSpan>();
        var release = await PublicReleaseAcquisition.ResolveAsync(client, "1.9.20", default,
            delay: (duration, _) => { delays.Add(duration); return Task.CompletedTask; });
        Assert.Equal("v1.9.20", release.Tag);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(delays));
        Assert.All(handler.Requests, uri => Assert.EndsWith("/releases/tags/v1.9.20", uri.AbsoluteUri));
    }

    /// <summary>Unadvertised releases and explicit user cancellation never become retry or fallback authority.</summary>
    [Fact]
    public async Task Metadata404AndCancellationDoNotRetry()
    {
        using var missing = new ResponseSequence(_ => new(HttpStatusCode.NotFound));
        using var client = new HttpClient(missing);
        await Assert.ThrowsAsync<AcquisitionException>(() => PublicReleaseAcquisition.ResolveAsync(client, "latest", default, delay: NoDelay));
        Assert.Equal(1, missing.Calls);
        using var cancellation = new CancellationTokenSource();
        using var failed = new ResponseSequence(_ => { cancellation.Cancel(); throw new OperationCanceledException("private-token", cancellation.Token); });
        using var cancelledClient = new HttpClient(failed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PublicReleaseAcquisition.ResolveAsync(cancelledClient, "latest", cancellation.Token, delay: NoDelay));
        Assert.Equal(1, failed.Calls);
    }

    /// <summary>Authenticated asset propagation and interrupted reads restart private bytes, then verify the complete download.</summary>
    [Fact]
    public async Task AssetRetryDiscardsPartialBytesAndConverges()
    {
        var bytes = Encoding.UTF8.GetBytes("complete verified archive bytes");
        var release = DownloadIdentity(bytes);
        using var directory = new DownloadDirectory();
        using var handler = new ResponseSequence(attempt => attempt switch
        {
            1 => new(HttpStatusCode.NotFound),
            2 => new(HttpStatusCode.OK) { Content = new StreamContent(new ResetStream(bytes)) },
            _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
        });
        using var client = new HttpClient(handler);
        var path = Path.Combine(directory.Path, "download.gz");
        await PublicReleaseAcquisition.DownloadAsync(client, release, path, default, delay: NoDelay);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(ProtectedFiles.PrivateFile, File.GetUnixFileMode(path));
        Assert.Equal(3, handler.Calls);
        Assert.All(handler.Requests, uri => Assert.Equal(PublicRelease.Repository + "/releases/download/" + release.Tag + "/" + release.Asset, uri.AbsoluteUri));
    }

    /// <summary>Download exhaustion describes the provider and plain setup recovery, without creating authoritative state.</summary>
    [Fact]
    public async Task DownloadFailureReportsPlainSetupRecovery()
    {
        using var directory = new DownloadDirectory();
        using var handler = new ResponseSequence(_ => new(HttpStatusCode.BadGateway));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<AcquisitionException>(() => PublicReleaseAcquisition.DownloadAsync(client,
            DownloadIdentity([1]), Path.Combine(directory.Path, "download.gz"), default, delay: NoDelay));
        var terminal = new FailureTerminal();
        new Setup(new FailureProcess(), terminal).ReportFailure(error, false);
        Assert.Equal(4, handler.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
        Assert.Contains("download the release from GitHub", terminal.Errors);
        Assert.Contains("Setup has not started", terminal.Errors);
        Assert.Contains("same 'wayfarerctl setup' command", terminal.Errors);
        Assert.DoesNotContain("doctor", terminal.Errors);
        Assert.DoesNotContain("--resume", terminal.Errors);
    }

    /// <summary>Verified transport bytes still fail closed when the archive is unsafe; archive parsing is never retried.</summary>
    [Fact]
    public async Task ArchiveFailureIsSafeAndNeverRetried()
    {
        var bytes = Encoding.UTF8.GetBytes("private-token malformed gzip bytes");
        var release = DownloadIdentity(bytes);
        var metadata = Metadata();
        metadata["assets"] = new[] { new { name = release.Asset, state = "uploaded", size = release.Size, digest = release.Digest } };
        using var directory = new DownloadDirectory();
        using var handler = new ResponseSequence(attempt => new(HttpStatusCode.OK)
        { Content = new ByteArrayContent(attempt == 1 ? JsonSerializer.SerializeToUtf8Bytes(metadata) : bytes) });
        using var client = new HttpClient(handler);
        var progress = new List<string>();
        var error = await Assert.ThrowsAsync<AcquisitionException>(() => PublicReleaseAcquisition.StageAsync(directory.Path, "latest", default,
            progress.Add, NoDelay, client));
        var terminal = new FailureTerminal();
        new Setup(new FailureProcess(), terminal).ReportFailure(error, false);
        Assert.Equal(2, handler.Calls);
        Assert.Contains("Unpacking the verified download...", progress);
        Assert.Contains("safely unpack or validate", terminal.Errors);
        Assert.Contains("Do not bypass", terminal.Errors);
        Assert.DoesNotContain("Retrying", string.Join('\n', progress));
        Assert.DoesNotContain("private-token", terminal.Errors);
    }

    /// <summary>Failed exact pulls retain release authority and never forward private child output.</summary>
    [Fact]
    public async Task ExactImagePullExhaustionRetainsReleaseAndPlainSetupGuidance()
    {
        var runner = new FailureProcess();
        var reference = "ghcr.io/stef-k/wayfarer@sha256:" + new string('a', 64);
        var error = await Assert.ThrowsAsync<AcquisitionException>(() => new PublicReleaseAcquisition(runner, delay: NoDelay)
            .PullImageAsync("/private/empty-client-config", "linux/amd64", reference, default));
        var terminal = new FailureTerminal();
        new Setup(runner, terminal).ReportFailure(error, false);
        Assert.Equal(4, runner.Calls.Count);
        Assert.All(runner.Calls, call => Assert.Equal(new[] { "--config", "/private/empty-client-config", "pull", "--platform", "linux/amd64", reference }, call));
        Assert.Contains("verified release is safely retained", terminal.Errors);
        Assert.Contains("ghcr.io", terminal.Errors);
        Assert.Contains("Setup has not started", terminal.Errors);
        Assert.DoesNotContain("--resume", terminal.Errors);
        Assert.DoesNotContain("private-child-secret", terminal.Errors + terminal.Output);
    }

    /// <summary>Protected setup execution does not retry a mutation or fall through to the generic CLI catch.</summary>
    [Fact]
    public async Task ConfiguredFailureReportsResumeWithoutRetryOrSecretDisclosure()
    {
        var runner = new FailureProcess { Error = new IOException("private-child-secret private-admin-secret") };
        var terminal = new FailureTerminal();
        var config = new Deployment { Bundle = "/retained/bundle", Hostname = "wayfarer.example.org", AppDigest = "sha256:" + new string('a', 64) };
        Assert.Equal(1, await new Setup(runner, terminal).FinishAsync("/installation", config, new(), "private-admin-secret", false, default));
        Assert.Single(runner.Calls);
        Assert.Contains("checking installation settings", terminal.Errors);
        Assert.Contains("Setup has started", terminal.Errors);
        Assert.Contains("'wayfarerctl setup --resume'", terminal.Errors);
        Assert.DoesNotContain("same 'wayfarerctl setup' command", terminal.Errors);
        Assert.DoesNotContain("private-admin-secret", terminal.Errors + terminal.Output);
        Assert.DoesNotContain("private-child-secret", terminal.Errors + terminal.Output);
        Assert.DoesNotContain("Operation failed.", terminal.Errors);
    }

    /// <summary>The plain-setup entry guard permits preparation residue but refuses protected installation state.</summary>
    [Fact]
    public void PlainSetupAllowsPreparationResidueButNeverInstallationOverwrite()
    {
        using var directory = new DownloadDirectory();
        var releases = Path.Combine(directory.Path, "releases");
        Directory.CreateDirectory(releases);
        foreach (var stage in new[] { ".acquire-test", ".pull-test", ".stage-test", "v1.9.20" })
            Directory.CreateDirectory(Path.Combine(releases, stage));
        File.WriteAllText(Path.Combine(releases, ".stage-test.json"), "retained placement receipt");
        File.WriteAllText(Path.Combine(directory.Path, "operation.lock"), "");
        Setup.RequireFreshState(directory.Path);
        Assert.False(Setup.HasProtectedState(directory.Path));
        File.WriteAllText(Path.Combine(directory.Path, "installation.json"), "protected state must not be replaced");
        Assert.True(Setup.HasProtectedState(directory.Path));
        var terminal = new FailureTerminal();
        new Setup(new FailureProcess(), terminal).ReportFailure(new IOException("private lock failure"), Setup.HasProtectedState(directory.Path));
        Assert.Contains("'wayfarerctl setup --resume'", terminal.Errors);
        var error = Assert.Throws<UsageException>(() => Setup.RequireFreshState(directory.Path));
        Assert.Contains("never overwrite", error.Message);
        Assert.Equal("protected state must not be replaced", File.ReadAllText(Path.Combine(directory.Path, "installation.json")));
        Assert.True(Directory.Exists(Path.Combine(releases, ".stage-test")));
    }

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
            var error = await Assert.ThrowsAsync<AcquisitionException>(() => PublicReleaseAcquisition.DownloadAsync(client,
                release with { Digest = "sha256:" + new string('0', 64) }, Path.Combine(directory, "bad-hash"), default, delay: NoDelay));
            Assert.Equal(2, handler.Calls); // One valid download and one terminal integrity failure.
            Assert.Contains("published integrity", error.Message);
            Assert.Contains("Do not bypass", error.NextAction);
            using var input = new MemoryStream(bytes);
            using var output = new MemoryStream();
            await Assert.ThrowsAsync<InvalidDataException>(() => PublicReleaseAcquisition.CopyAsync(input, output, bytes.Length - 1, default));
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
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.StartsWith("https://github.com/stef-k/Wayfarer/releases/download/v1.9.20/", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    /// <summary>Injects responses and transport failures into the existing HttpClient boundary.</summary>
    private sealed class ResponseSequence(Func<int, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(reply(++Calls));
        }
    }

    /// <summary>Published facts for a controlled exact response body.</summary>
    private static PublicRelease DownloadIdentity(byte[] bytes) => new("1.9.20", "v1.9.20", "wayfarer-v1.9.20-linux-amd64.tar.gz", bytes.Length,
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));

    /// <summary>Replace waiting only, keeping the production retry classifier and attempt budget.</summary>
    private static Task NoDelay(TimeSpan duration, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }

    /// <summary>Own only the private ephemeral tree created for this test.</summary>
    private sealed class DownloadDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wayfarer-download-" + Guid.NewGuid().ToString("N"));
        public DownloadDirectory() => Directory.CreateDirectory(Path, ProtectedFiles.PrivateDirectory);
        public void Dispose() => Directory.Delete(Path, true);
    }

    /// <summary>Return some bytes, then a connection reset; a second HTTP attempt must start from zero.</summary>
    private sealed class ResetStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => Position == 0
            ? base.ReadAsync(buffer[..5], token) : throw new IOException("private-token connection reset");
    }

    /// <summary>Private captured output must never become an operator-facing explanation.</summary>
    private sealed class FailureProcess : IProcessRunner
    {
        public List<string[]> Calls { get; } = [];
        public Exception? Error { get; init; }
        public Task<ProcessResult> RunAsync(string[] args, string? input, CancellationToken cancellation, Action<string>? lineOutput = null)
        {
            Calls.Add(args);
            if (Error is not null) throw Error;
            return Task.FromResult(new ProcessResult(1, "private-child-secret"));
        }
    }

    /// <summary>Capture exactly the sanitized operator streams; no password or interactive input is needed.</summary>
    private sealed class FailureTerminal : ITerminal
    {
        public bool Interactive => false;
        public string Errors { get; private set; } = "";
        public string Output { get; private set; } = "";
        public void Error(string message) => Errors += message + "\n";
        public void Write(string message) => Output += message + "\n";
        public string? Read(string prompt) => throw new InvalidOperationException();
        public string Password(bool fromStdin) => throw new InvalidOperationException();
    }
}
