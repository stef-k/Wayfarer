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

    [Fact]
    public async Task FailedMigrationNeverSeedsBootstrapsOrStartsIngress()
    {
        var process = new FakeProcess { FailAt = 4 };
        await Assert.ThrowsAsync<IOException>(() => new Setup(process, new FakeTerminal()).ExecuteAsync("/etc/wayfarer", Config(), "hidden", default));
        Assert.Equal(4, process.Calls.Count);
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
