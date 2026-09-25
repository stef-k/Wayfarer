using WayfarerCtl;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>CLI contract tests at the command/process seam, without Docker or application-domain mocks.</summary>
public sealed class WayfarerCtlTests
{
    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help setup")]
    [InlineData("setup --help")]
    [InlineData("user --help")]
    [InlineData("user reset-password --help")]
    public async Task HelpDoesNotRequireHostOrDeployment(string arguments)
    {
        var process = new FakeProcess(); var terminal = new FakeTerminal();
        Assert.Equal(0, await new Cli(process, terminal).RunAsync(arguments.Split(' ')));
        Assert.Contains("Exit: 0", terminal.Output);
        Assert.Empty(process.Calls);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("user reset-password admin plaintext")]
    [InlineData("logs db --tail 0")]
    [InlineData("logs db --tail 10001")]
    [InlineData("logs other")]
    [InlineData("status --unknown")]
    [InlineData("setup --unknown value")]
    public async Task InvalidArgumentsFailBeforeHostAccess(string arguments)
    {
        var process = new FakeProcess(); var terminal = new FakeTerminal();
        Assert.Equal(2, await new Cli(process, terminal).RunAsync(arguments.Split(' ')));
        Assert.Empty(process.Calls);
        Assert.DoesNotContain("plaintext", terminal.Errors);
    }

    [Fact]
    public async Task RedirectedBareInvocationNeverReadsInput()
    {
        var terminal = new FakeTerminal();
        Assert.Equal(0, await new Cli(new FakeProcess(), terminal).RunAsync([]));
        Assert.Equal(0, terminal.Reads);
    }

    [Fact]
    public async Task MenuUsesSameHelpHandlerAndEofExits()
    {
        var terminal = new FakeTerminal { Interactive = true };
        terminal.Input.Enqueue("9"); terminal.Input.Enqueue(null);
        Assert.Equal(0, await new Cli(new FakeProcess(), terminal).RunAsync([]));
        Assert.Contains(Help.Text(""), terminal.Output);
        Assert.Equal(2, terminal.Reads);
    }

    [Fact]
    public async Task CancelledMenuTerminates()
    {
        var terminal = new FakeTerminal { Interactive = true };
        Assert.Equal(1, await new Cli(new FakeProcess(), terminal).RunAsync([], new CancellationToken(true)));
    }

    [Theory]
    [InlineData("managed", 8)]
    [InlineData("external", 7)]
    public async Task SetupOrdersAuthorityAndKeepsPasswordOutOfArguments(string mode, int count)
    {
        var process = new FakeProcess(); var terminal = new FakeTerminal();
        await new Setup(process, terminal).ExecuteAsync("/etc/wayfarer", Config() with { Mode = mode }, "secret-test", default);
        Assert.Equal(count, process.Calls.Count);
        Assert.Contains("config --quiet", Join(process.Calls[0]));
        Assert.Contains("180 db", Join(process.Calls[1]));
        Assert.Contains("--user 0", Join(process.Calls[2]));
        Assert.Contains("database migrate", Join(process.Calls[3]));
        Assert.Contains("database seed", Join(process.Calls[4]));
        Assert.Contains("admin bootstrap admin --stdin", Join(process.Calls[5]));
        Assert.Equal("secret-test", process.Inputs[5]);
        Assert.All(process.Calls, call => Assert.DoesNotContain("secret-test", Join(call)));
        Assert.DoesNotContain("secret-test", terminal.Output);
        Assert.DoesNotContain(process.Calls, call => call.Contains("down") || call.Contains("-v"));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task FailedMaintenanceNeverRunsSubsequentStepsOrIngress(int failedStep)
    {
        var process = new FakeProcess { FailAt = failedStep };
        await Assert.ThrowsAsync<IOException>(() => new Setup(process, new FakeTerminal()).ExecuteAsync("/etc/wayfarer", Config(), "hidden", default));
        Assert.Equal(failedStep, process.Calls.Count);
    }

    [Theory]
    [InlineData("2.24.3", 0)]
    [InlineData("1.29.0", 0)]
    [InlineData("bad", 0)]
    [InlineData("2.40.3", 1)]
    public async Task DockerPreflightRejectsOldUnavailableCompose(string version, int code)
    {
        var process = new FakeProcess { Reply = args => args[0] == "info" ? new(0, "linux/x86_64") : new(code, version) };
        await Assert.ThrowsAsync<UsageException>(() => new Preflight(process).DockerAsync(default));
    }

    [Fact]
    public async Task ExistingResourcesRefuseFreshSetup()
    {
        var process = new FakeProcess { Reply = _ => new(0, "existing") };
        await Assert.ThrowsAsync<UsageException>(() => new Preflight(process).FreshAsync(Config(), default));
        Assert.Single(process.Calls);
    }

    [Theory]
    [InlineData("172.30.64.0/24", "172.30.0.0/16", true)]
    [InlineData("172.30.64.0/24", "172.30.64.128/25", true)]
    [InlineData("172.30.64.0/24", "172.30.65.0/24", false)]
    public void NetworkCollisionUsesBothIntervalDirections(string first, string second, bool expected) =>
        Assert.Equal(expected, Preflight.Overlaps(first, second));

    [Fact]
    public async Task DefaultHostNetworksWithoutIpamAreSupported()
    {
        var process = new FakeProcess { Reply = args => args.Contains("ls") ? new(0, "host\nnone") :
            new(0, "[{\"Labels\":{},\"IPAM\":{\"Config\":null}}]") };
        await new Preflight(process).NetworksAsync(Config(), default, installed: true);
        Assert.Equal(3, process.Calls.Count);
    }

    [Fact]
    public void DeploymentRejectsMutableImageAndNonpublicHostname()
    {
        Assert.Throws<UsageException>(() => (Config() with { AppDigest = "latest" }).Validate());
        Assert.Throws<UsageException>(() => (Config() with { Hostname = "localhost" }).Validate());
        Assert.Throws<UsageException>(() => (Config() with { Mode = "native" }).Validate());
        Assert.Throws<UsageException>(() => (Config() with { Schema = 2 }).Validate());
        Config().Validate();
    }

    [Fact]
    public void ModeSelectionAndProjectAreExplicit()
    {
        var config = Config();
        Assert.Contains("managed", config.Compose("/etc/wayfarer", "stop"));
        var external = (config with { Mode = "external" }).Compose("/etc/wayfarer", "stop");
        Assert.DoesNotContain("--profile", external);
        Assert.Contains("/bundle/external.yaml", external);
        Assert.Contains("--project-name", external);
    }

    [Theory]
    [InlineData("find", 0)]
    [InlineData("find", 1)]
    [InlineData("reset-password", 0)]
    [InlineData("reset-password", 1)]
    public async Task UserBridgeDelegatesAndMapsAuthorityResults(string operation, int code)
    {
        var process = new FakeProcess { Reply = _ => new(code, code == 0 ? "{\"UserName\":\"admin\"}" : "") };
        var terminal = new FakeTerminal();
        var result = await new Cli(process, terminal).UserAsync("/etc/wayfarer", Config(),
            ["user", operation, "admin", "--password-stdin"], default);
        Assert.Equal(code, result);
        Assert.Single(process.Calls);
        var command = Join(process.Calls[0]);
        Assert.Contains(operation == "find" ? "user find admin" : "admin reset admin --stdin", command);
        Assert.DoesNotContain("protected", command);
        Assert.DoesNotContain("protected", terminal.Output + terminal.Errors);
        Assert.Equal(operation == "find" ? null : "protected", process.Inputs[0]);
    }

    [Fact]
    public async Task OccupiedExternalPortFailsBeforeMutation()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        Assert.Throws<UsageException>(() => Preflight.CheckPorts(Config() with { Mode = "external", LoopbackPort = port }));
        await Task.CompletedTask;
    }

    [Fact]
    public void UnsupportedHostFailsClosed()
    {
        Assert.Throws<UsageException>(() => Preflight.CheckPlatform(false, System.Runtime.InteropServices.Architecture.X64));
        Assert.Throws<UsageException>(() => Preflight.CheckPlatform(true, System.Runtime.InteropServices.Architecture.Arm64));
        Preflight.CheckPlatform(true, System.Runtime.InteropServices.Architecture.X64);
    }

    [Fact]
    public void BundleCannotIgnoreImmutableInputAndUseMutableImage()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            {"services":{"wayfarer":{"image":"ghcr.io/stef-k/wayfarer:latest","platform":"linux/amd64"}}}
            """);
        Assert.Throws<UsageException>(() => Preflight.VerifyImages(Config(), document.RootElement));
    }

    [Fact]
    public async Task UnavailableDockerFailsBeforeOtherPreflightWork()
    {
        var process = new FakeProcess { Reply = _ => new(1, "unavailable") };
        await Assert.ThrowsAsync<UsageException>(() => new Preflight(process).DockerAsync(default));
        Assert.Single(process.Calls);
    }

    /// <summary>A new invocation consumes the durable checkpoint, preserving completed mutations after each boundary.</summary>
    [Theory]
    [InlineData(5, 1)] // Migration committed; seed fails.
    [InlineData(6, 2)] // Seed committed; bootstrap fails before success is known.
    [InlineData(7, 3)] // Bootstrap committed; web readiness fails.
    [InlineData(8, 3)] // Web ready; managed proxy fails.
    public async Task ResumeSkipsCompletedMutationsAndPreservesIdentity(int failedStep, int completed)
    {
        var original = new SetupProgress { Fingerprint = "original-config-and-secret-fingerprint" };
        var receipt = "";
        void Save() => receipt = System.Text.Json.JsonSerializer.Serialize(original);
        var first = new FakeProcess { FailAt = failedStep };
        await Assert.ThrowsAsync<IOException>(() => new Setup(first, new FakeTerminal())
            .ExecuteAsync("/etc/wayfarer", Config(), "protected", default, original, Save));
        var restored = System.Text.Json.JsonSerializer.Deserialize<SetupProgress>(receipt)!;
        Assert.Equal(completed, restored.Completed);
        var retry = new FakeProcess(); // An uncertain bootstrap lookup reports the existing account.
        await new Setup(retry, new FakeTerminal()).ExecuteAsync("/etc/wayfarer", Config(), completed == 1 ? "protected" : "", default, restored);
        Assert.DoesNotContain(retry.Calls, call => Join(call).Contains("database migrate"));
        if (completed >= 2) Assert.DoesNotContain(retry.Calls, call => Join(call).Contains("database seed"));
        if (completed >= 2) Assert.DoesNotContain(retry.Calls, call => Join(call).Contains("admin bootstrap"));
        else Assert.Single(retry.Calls.Where(call => Join(call).Contains("admin bootstrap")));
        Assert.Equal(original.Fingerprint, restored.Fingerprint);
        if (completed >= 2) Assert.All(retry.Inputs, Assert.Null);
        Assert.DoesNotContain(retry.Calls, call => call.Contains("down") || call.Contains("-v"));
        Assert.Equal(3, restored.Completed);
    }

    /// <summary>Lost/failed admin results cannot implicitly reset or retry credentials.</summary>
    [Fact]
    public async Task UncertainAdminRequiresExplicitRetryWhenLookupCannotConfirm()
    {
        var progress = new SetupProgress { Completed = 2, AdminStarted = true };
        var process = new FakeProcess { Reply = args => new(Join(args).Contains("user find") ? 1 : 0, "") };
        await Assert.ThrowsAsync<UsageException>(() => new Setup(process, new FakeTerminal())
            .ExecuteAsync("/etc/wayfarer", Config(), "", default, progress));
        Assert.DoesNotContain(process.Calls, call => Join(call).Contains("admin bootstrap") || call.Last() == "caddy");
        var retry = new FakeProcess { Reply = args => new(Join(args).Contains("user find") ? 1 : 0, "") };
        await new Setup(retry, new FakeTerminal()).ExecuteAsync("/etc/wayfarer", Config(), "new-protected", default, progress, retryAdmin: true);
        Assert.Single(retry.Calls.Where(call => Join(call).Contains("admin bootstrap")));
        Assert.DoesNotContain(retry.Calls, call => Join(call).Contains("admin reset"));
        Assert.Equal("new-protected", retry.Inputs.Single(value => value is not null));
    }

    /// <summary>A missing migrated cluster cannot be silently replaced with a fresh volume.</summary>
    [Fact]
    public async Task ResumeRefusesMissingDurableDatabase()
    {
        var process = new FakeProcess();
        await Assert.ThrowsAsync<UsageException>(() => new Preflight(process).ResumeAsync("/etc/wayfarer", Config(), default, true));
        Assert.Single(process.Calls);
    }

    /// <summary>Project-name collisions do not authorize adopting unrelated containers or volumes.</summary>
    [Theory]
    [InlineData("volume", "{\"Name\":\"wayfarer_db-data\",\"Labels\":{}}")]
    [InlineData("network", "{\"Name\":\"wayfarer_edge\",\"Labels\":{\"com.docker.compose.project\":\"foreign\"}}")]
    [InlineData("container", "{\"Config\":{\"Labels\":{\"com.docker.compose.project\":\"wayfarer\",\"com.docker.compose.project.working_dir\":\"/foreign\"}}}")]
    public void ResumeRefusesForeignResources(string kind, string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Throws<UsageException>(() => Preflight.VerifyRetainedResource(Config(), kind, document.RootElement));
    }

    /// <summary>Continuation has no surface for changing the original installation choices.</summary>
    [Theory]
    [InlineData("--resume --hostname other.example.org")]
    [InlineData("--retry-admin")]
    public void ResumeRefusesChangedChoices(string options) =>
        Assert.Throws<UsageException>(() => Setup.Options(options.Split(' ')));

    private static Deployment Config() => new() { Bundle = "/bundle", Hostname = "wayfarer.example.org", AppDigest = "sha256:" + new string('a', 64) };
    private static string Join(string[] args) => string.Join(' ', args);

    /// <summary>Captures only process requests; application semantics remain outside this test.</summary>
    private sealed class FakeProcess : IProcessRunner
    {
        public List<string[]> Calls { get; } = [];
        public List<string?> Inputs { get; } = [];
        public int FailAt { get; init; }
        public Func<string[], ProcessResult>? Reply { get; init; }
        public Task<ProcessResult> RunAsync(string[] args, string? input, CancellationToken cancellation, Action<string>? lineOutput = null)
        {
            cancellation.ThrowIfCancellationRequested(); Calls.Add(args); Inputs.Add(input);
            return Task.FromResult(Reply?.Invoke(args) ?? new ProcessResult(Calls.Count == FailAt ? 1 : 0, ""));
        }
    }

    /// <summary>Deterministic line-oriented terminal, including redirected input and EOF.</summary>
    private sealed class FakeTerminal : ITerminal
    {
        public bool Interactive { get; init; }
        public string Output { get; private set; } = "";
        public string Errors { get; private set; } = "";
        public int Reads { get; private set; }
        public Queue<string?> Input { get; } = new();
        public void Write(string message) => Output += message + "\n";
        public void Error(string message) => Errors += message + "\n";
        public string? Read(string prompt) { Reads++; return Input.Dequeue(); }
        public string Password(bool fromStdin) => "protected";
    }
}
