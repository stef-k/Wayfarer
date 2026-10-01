using System.Text.Json;
using WayfarerCtl;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Protected setup provisioning needs real Linux root ownership; CI runs this selection separately.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[Trait("Category", "RequiresRoot")]
public sealed class SetupProvisioningTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "wayfarer-setup-" + Guid.NewGuid().ToString("N"));
    private readonly string root;
    private readonly Deployment config;

    /// <summary>Own one private tree and only the five bundle inputs consumed by the initial setup receipt.</summary>
    public SetupProvisioningTests()
    {
        ProtectedFiles.RequireRoot();
        Directory.CreateDirectory(directory, ProtectedFiles.PrivateDirectory);
        root = Path.Combine(directory, "installation");
        Directory.CreateDirectory(root, ProtectedFiles.PrivateDirectory);
        var bundle = Path.Combine(directory, "bundle");
        foreach (var file in new[] { "compose.yaml", "external.yaml", "caddy/Caddyfile", "db/20-wayfarer.sh", "config/deployment.env.example" })
        {
            var path = Path.Combine(bundle, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file);
        }
        config = new Deployment { Bundle = bundle, Hostname = "wayfarer.example.org", Mode = "external",
            AppDigest = "sha256:" + new string('a', 64), Platform = WayfarerRecovery.NativePlatform.Current };
    }

    /// <summary>An older unreceipted partial installation must reach explicit reconciliation, never a resume loop.</summary>
    [Fact]
    public async Task UnreceiptedProtectedStateDoesNotRecommendAnotherResume()
    {
        var original = JsonSerializer.Serialize(config);
        ProtectedFiles.Create(Path.Combine(root, "installation.json"), original);
        var terminal = new CapturedTerminal();
        Assert.NotEqual(0, await new Setup(new SetupProcess(config), terminal).RunAsync(root, ["--resume"], default));
        Assert.Contains("cannot safely resume", terminal.Errors);
        Assert.DoesNotContain("Then run 'wayfarerctl setup --resume'", terminal.Errors);
        Assert.Equal(original, File.ReadAllText(Path.Combine(root, "installation.json")));
    }

    /// <summary>Faults after config, within credentials/env and before/during receipt creation leave plain setup usable.</summary>
    [Theory]
    [InlineData("installation.json")]
    [InlineData("db-app-password")]
    [InlineData("deployment.env")]
    [InlineData("setup-progress.json")]
    [InlineData("receipt-write")]
    public void PreparationInterruptionCanRetryWithoutProtectedPartialState(string point)
    {
        Assert.ThrowsAny<Exception>(() => SetupProvisioning.Create(root, config, path =>
        {
            if (point == "receipt-write" && Path.GetFileName(path) == "setup-progress.json")
                Directory.CreateDirectory(path); // Exercise an actual exclusive receipt-create failure.
            else if (Path.GetFileName(path) == point) throw new IOException("private injected failure");
        }));
        Assert.False(Setup.HasProtectedState(root));
        Setup.RequireFreshState(root);
        Assert.Equal(Setup.Recovery.Retry, Setup.RecoveryForRoot(root));
        var progress = SetupProvisioning.Create(root, config);
        Assert.Equal(progress.Fingerprint, SetupProgress.Load(root, Deployment.Load(root)).Fingerprint);
        Deployment.CheckSecrets(root);
    }

    /// <summary>The recommended resume command publishes original bytes and reaches the canonical execution boundary.</summary>
    [Theory]
    [InlineData("installation.json")]
    [InlineData("db-app-password")]
    [InlineData("deployment.env")]
    [InlineData("setup-progress.json")]
    public async Task PublicationInterruptionResumesOriginalInputs(string point)
    {
        Assert.Throws<IOException>(() => SetupProvisioning.Create(root, config, path =>
        {
            if (path.StartsWith(root + "/", StringComparison.Ordinal) &&
                !path.StartsWith(Path.Combine(root, "releases") + "/", StringComparison.Ordinal) && Path.GetFileName(path) == point)
                throw new IOException("private injected failure");
        }));
        var source = Path.Combine(root, SetupProvisioning.Name);
        var original = new[] { "installation.json", "deployment.env", "secrets/db-password", "secrets/db-app-password", "secrets/app-password" }
            .ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(source, name)));
        Assert.Equal(Setup.Recovery.Resume, Setup.RecoveryForRoot(root));
        var process = new SetupProcess(config);
        var terminal = new CapturedTerminal();
        Assert.Equal(1, await new Setup(process, terminal).RunAsync(root, ["--resume"], default));
        Assert.True(process.ReachedExecution);
        Assert.Contains("checking installation settings", terminal.Errors);
        Assert.Contains("Then run 'wayfarerctl setup --resume'", terminal.Errors);
        Assert.DoesNotContain("private injected failure", terminal.Errors);
        foreach (var (name, bytes) in original) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(root, name)));
        Assert.Equal(0, SetupProgress.Load(root, Deployment.Load(root)).Completed);
    }

    /// <summary>Receipt-owned continuation never repairs a credential changed after publication began.</summary>
    [Fact]
    public async Task ChangedPublishedCredentialRequiresReconciliationWithoutOverwrite()
    {
        Assert.Throws<IOException>(() => SetupProvisioning.Create(root, config, path =>
        {
            if (path == Path.Combine(root, "secrets/db-app-password")) throw new IOException();
        }));
        var credential = Path.Combine(root, "secrets/db-app-password");
        File.WriteAllText(credential, new string('B', 64));
        var original = File.ReadAllBytes(credential);
        var terminal = new CapturedTerminal();
        var process = new SetupProcess(config);
        Assert.NotEqual(0, await new Setup(process, terminal).RunAsync(root, ["--resume"], default));
        Assert.False(process.ReachedExecution);
        Assert.Contains("cannot safely resume", terminal.Errors);
        Assert.DoesNotContain("Then run 'wayfarerctl setup --resume'", terminal.Errors);
        Assert.Equal(original, File.ReadAllBytes(credential));
        Assert.False(File.Exists(Path.Combine(root, "deployment.env")));
    }

    /// <summary>Supply only read-only prerequisites; stop at the first canonical mutation to expose repaired setup inputs.</summary>
    private sealed class SetupProcess(Deployment config) : IProcessRunner
    {
        public bool ReachedExecution { get; private set; }
        public Task<ProcessResult> RunAsync(string[] args, string? input, CancellationToken token, Action<string>? lineOutput = null)
        {
            if (args.Contains("--quiet")) ReachedExecution = true;
            var output = args[0] == "info" ? config.RuntimePlatform == "linux/amd64" ? "linux/x86_64" : "linux/aarch64" :
                args.Contains("version") ? "2.24.4" : args.Contains("json") ? JsonSerializer.Serialize(new { services = new
                {
                    wayfarer = new { image = "ghcr.io/stef-k/wayfarer@" + config.AppDigest, platform = config.RuntimePlatform },
                    db = new { image = "ghcr.io/stef-k/wayfarer-db@" + config.DbDigest, platform = config.RuntimePlatform }
                } }) : "";
            return Task.FromResult(new ProcessResult(args.Contains("--quiet") ? 1 : 0, output));
        }
    }

    /// <summary>Capture safe guidance and provide a protected password without exposing it in output.</summary>
    private sealed class CapturedTerminal : ITerminal
    {
        public bool Interactive => false;
        public string Errors { get; private set; } = "";
        public void Error(string message) => Errors += message + "\n";
        public void Write(string message) { }
        public string? Read(string prompt) => throw new InvalidOperationException();
        public string Password(bool fromStdin) => "private-admin-secret";
    }

    /// <summary>Delete only this fixture's private temporary tree.</summary>
    public void Dispose() => Directory.Delete(directory, true);
}
