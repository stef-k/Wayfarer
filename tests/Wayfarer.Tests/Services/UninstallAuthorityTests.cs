using System.Text.Json;
using WayfarerCtl;
using WayfarerRecovery;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Protected plan/receipt authority uses real Linux ownership and the existing fake process seam.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[Trait("Category", "RequiresRoot")]
public sealed class UninstallAuthorityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "wayfarer-uninstall-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ReleaseBundleTests releases = new();
    private readonly Deployment config;
    private readonly ReadOnlyRunner runner;
    private readonly UninstallPreparation preparation;

    /// <summary>Bind hosted planning to synthetic retained operator bytes and the production secret provisioner; never start a Docker service.</summary>
    public UninstallAuthorityTests()
    {
        ProtectedFiles.RequireRoot();
        Directory.CreateDirectory(root, ProtectedFiles.PrivateDirectory);
        var bundle = releases.RetainOperator(root, "1.9.22", runningOperator: false, stable: true);
        config = UninstallPlanningTests.Config() with { Bundle = bundle.Directory, Release = ReleaseAuthority.From(bundle),
            AppDigest = bundle.Manifest.Images.PlatformDigest, DbDigest = bundle.Manifest.Images.DatabaseDigest, Platform = NativePlatform.Current };
        ProtectedFiles.Create(Path.Combine(root, "installation.json"), JsonSerializer.Serialize(config));
        ProtectedFiles.Create(Path.Combine(root, "deployment.env"), config.EnvironmentFile(root));
        ProtectedFiles.Create(Path.Combine(root, "setup-complete"), "1\n");
        ProtectedFiles.CreateSecrets(root);
        Directory.CreateDirectory(Path.Combine(root, "recovery-control"), ProtectedFiles.PrivateDirectory);
        runner = new ReadOnlyRunner(config, root + "-docker");
        foreach (var role in UninstallInventory.Roles(config))
            Directory.CreateDirectory(Path.Combine(runner.DockerRoot, "volumes", ActiveStorage.Volume(config, role), "_data"));
        preparation = new UninstallPreparation(runner) { ExecutablePath = Path.Combine(bundle.Directory, "wayfarerctl") };
    }

    /// <summary>Plan publication/load are protected and deterministic, and command discovery performs no destructive calls.</summary>
    [Fact]
    public async Task PlanningPublishesOnlyProtectedEvidence()
    {
        var plan = await Prepare();
        Assert.Equal(Guid.Empty, plan.Current.Installation);
        var path = UninstallPreparation.PathFor(root, plan.Hash());
        ProtectedFiles.Check(path, 0);
        Assert.Equal(plan.Hash(), (await preparation.LoadAsync(root, plan.Hash())).Hash());
        Assert.Equal(UninstallState.None, UninstallReceipt.State(root));
        Assert.False(File.Exists(UninstallReceipt.PathFor(root)));
        var serialized = File.ReadAllText(path);
        foreach (var secret in Directory.EnumerateFiles(Path.Combine(root, "secrets"))) Assert.DoesNotContain(File.ReadAllText(secret), serialized);
        Assert.All(runner.Commands, args => Assert.Contains(args[0], new[] { "info", "compose", "ps", "network", "volume", "container" }));
    }

    /// <summary>Equivalent JSON edits, environment drift, credential rotation and owner changes all invalidate accepted bytes.</summary>
    [Theory]
    [InlineData("installation")]
    [InlineData("environment")]
    [InlineData("secrets")]
    [InlineData("operator")]
    [InlineData("resource")]
    [InlineData("resource-inode")]
    public async Task StaleAuthorityFailsBeforeReceiptCreation(string changed)
    {
        var plan = await Prepare();
        switch (changed)
        {
            case "installation": File.AppendAllText(Path.Combine(root, "installation.json"), "\n"); break;
            case "environment": File.AppendAllText(Path.Combine(root, "deployment.env"), "\n"); break;
            case "secrets": File.WriteAllText(Path.Combine(root, "secrets/db-password"), new string('E', 64)); break;
            case "operator": File.WriteAllText(Path.Combine(config.Bundle, "wayfarerctl"), "replaced"); break;
            case "resource": runner.Replaced = true; break;
            case "resource-inode":
                var directory = Path.Combine(runner.DockerRoot, "volumes", ActiveStorage.Volume(config, "app-data"), "_data");
                Directory.Move(directory, directory + "-original");
                Directory.CreateDirectory(directory);
                break;
        }
        await Assert.ThrowsAnyAsync<Exception>(() => preparation.LoadAsync(root, plan.Hash()));
        Assert.False(File.Exists(UninstallReceipt.PathFor(root)));
    }

    /// <summary>Hash lookup does not trust plan contents, unsafe links, oversized evidence or changed serialized authority.</summary>
    [Theory]
    [InlineData("hash")]
    [InlineData("foreign-root")]
    [InlineData("oversized")]
    [InlineData("link")]
    [InlineData("malformed")]
    [InlineData("whitespace")]
    public async Task UntrustedPlanFilesAreRefused(string changed)
    {
        var plan = await Prepare();
        var path = UninstallPreparation.PathFor(root, plan.Hash());
        switch (changed)
        {
            case "hash": File.WriteAllText(path, JsonSerializer.Serialize(plan with { Operation = Guid.NewGuid() })); break;
            case "foreign-root": File.WriteAllText(path, JsonSerializer.Serialize(plan with { Root = "/other" })); break;
            case "oversized": File.WriteAllText(path, new string('x', 1048577)); break;
            case "link": File.Delete(path); File.CreateSymbolicLink(path, Path.Combine(root, "installation.json")); break;
            case "malformed": File.WriteAllText(path, "{}"); break;
            case "whitespace": File.AppendAllText(path, "\n"); break;
        }
        await Assert.ThrowsAnyAsync<Exception>(() => preparation.LoadAsync(root, plan.Hash()));
        if (changed == "link") File.Delete(path);
    }

    /// <summary>All conflicting lifecycle states refuse through their existing protected receipt/marker seams.</summary>
    [Theory]
    [InlineData("setup")]
    [InlineData("backup-transition.json")]
    [InlineData("backup-identity")]
    [InlineData("recovery-control/host-operation.json")]
    [InlineData("recovery-control/update-in-progress")]
    [InlineData("update")]
    [InlineData("restore")]
    [InlineData("uninstall")]
    public async Task UnresolvedLifecycleRefusesPlanning(string state)
    {
        switch (state)
        {
            case "setup": File.Delete(Path.Combine(root, "setup-complete")); break;
            case "update": WriteUpdate(); break;
            case "restore": WriteRestore(); break;
            case "uninstall":
                var plan = await Prepare();
                new UninstallReceipt { Plan = plan, PlanHash = plan.Hash() }.Save(root);
                break;
            default: ProtectedFiles.Create(Path.Combine(root, state), "unresolved"); break;
        }
        await Assert.ThrowsAnyAsync<Exception>(() => Prepare());
    }

    /// <summary>A failed inspection never becomes absence or permission to persist a cleanup plan.</summary>
    [Fact]
    public async Task DockerFailureDoesNotPublishPlan()
    {
        runner.FailInspection = true;
        await Assert.ThrowsAsync<UsageException>(() => Prepare());
        Assert.False(Directory.Exists(Path.Combine(root, "uninstall-plans")));
    }

    /// <summary>Persistence rejects a second plan, backward phases and falsely terminal state; preserved authority is explicit.</summary>
    [Fact]
    public async Task ReceiptPersistenceCannotSupersedeUnresolvedAuthority()
    {
        var plan = await Prepare();
        var receipt = new UninstallReceipt { Plan = plan, PlanHash = plan.Hash() };
        receipt.Save(root);
        Assert.Equal(UninstallState.Unresolved, UninstallReceipt.State(root));
        var other = plan with { Operation = Guid.NewGuid() };
        Assert.Throws<UsageException>(() => new UninstallReceipt { Plan = other, PlanHash = other.Hash() }.Save(root));
        receipt = receipt.Advance(UninstallPhase.Fenced);
        receipt.Save(root);
        Assert.Throws<UsageException>(() => (receipt with { Phase = UninstallPhase.Authorized }).Save(root));
        receipt = receipt.Advance(UninstallPhase.RuntimeRemoved);
        receipt.Save(root);
        receipt = receipt.Advance(UninstallPhase.Preserved);
        receipt.Save(root);
        Assert.Equal(UninstallState.Preserved, UninstallReceipt.State(root));
        Assert.Throws<UsageException>(() => (receipt with { Phase = UninstallPhase.RuntimeRemoved }).Save(root));
        await Assert.ThrowsAsync<UsageException>(() => Prepare());
        var purge = await preparation.PrepareAsync(root, UninstallOptions.Parse(["--purge", "--plan", "--without-backup"]));
        Assert.Equal(UninstallStartingState.Preserved, purge.StartingState);
        await Assert.ThrowsAsync<UsageException>(() => preparation.PrepareAsync(root, UninstallOptions.Parse(["--purge", "--plan", "--backup"])));
        runner.Replaced = true;
        await Assert.ThrowsAsync<UsageException>(() => preparation.PrepareAsync(root, UninstallOptions.Parse(["--purge", "--plan", "--without-backup"])));
    }

    /// <summary>A stopped capture helper enters inventory only with exact terminal update authority and fixed recovery mounts.</summary>
    [Fact]
    public void TerminalCaptureHelperRequiresReceiptAndMountAuthority()
    {
        var update = WriteUpdate();
        var name = config.Project + "-backup-" + Guid.NewGuid().ToString("N");
        update = update with { CaptureContainer = name, Phase = UpdatePhase.Aborted };
        update.Save(root);
        var current = update.Plan.Current;
        var policy = current.Backup!;
        var labels = new Dictionary<string, string> { ["com.docker.compose.project"] = current.Project,
            ["com.docker.compose.service"] = "backup-worker", ["com.docker.compose.project.working_dir"] = current.Bundle,
            ["com.docker.compose.project.config_files"] = current.Bundle + "/compose.yaml," + current.Bundle + "/external.yaml," +
                BackupCompose.DirectoryPath(root, policy) + "/compose.json" };
        object Bind(string source, string target, bool readOnly) => new { Type = "bind", Source = source, Destination = target, RW = !readOnly };
        var helper = JsonSerializer.SerializeToElement(new { Id = new string('f', 64), Name = "/" + name,
            Created = "2026-10-05T00:00:00Z", Image = new string('e', 64), State = new { Running = false },
            Config = new { Image = "ghcr.io/stef-k/wayfarer-db@" + current.DbDigest, Labels = labels,
                Cmd = new[] { "backup", "--host-operation", Guid.NewGuid().ToString("N") }, User = "1654:1654" },
            HostConfig = new { RestartPolicy = new { Name = "no" }, ReadonlyRootfs = true, Privileged = false },
            Mounts = new object[] { Bind(policy.Payload, "/worker/wayfarer-recovery", true),
                Bind(Path.Combine(BackupCompose.DirectoryPath(root, policy), "worker.json"), "/config/worker.json", true),
                Bind(policy.Destination, "/destination/slot", false), Bind(Path.Combine(root, "secrets/app-password"), "/run/secrets/app-password", true),
                Bind(Path.Combine(root, "recovery-control"), "/control", true),
                Bind(Path.Combine(root, "recovery-control/recovery.lock"), "/control/recovery.lock", false),
                new { Type = "volume", Name = ActiveStorage.Volume(current, "app-data"), Source = "/docker/app-data", Destination = "/source", RW = false } },
            NetworkSettings = new { Networks = new Dictionary<string, object> { [current.Project + "_backend"] = new { } } } });
        var history = UninstallHistory.Load(root, current);
        var volumes = new[] { UninstallPlanningTests.Volume(current, "db-data"), UninstallPlanningTests.Volume(current, "app-data") };
        var resources = UninstallInventory.Build(root, current, UninstallMode.Normal, history, [helper], [], volumes);
        Assert.Contains(resources, resource => resource.Name == name && resource.LifecycleOperation == update.Plan.Operation);
        var changed = helper.Deserialize<Dictionary<string, JsonElement>>()!;
        changed["Mounts"] = JsonSerializer.SerializeToElement(Array.Empty<object>());
        Assert.Throws<UsageException>(() => UninstallInventory.Build(root, current, UninstallMode.Normal, history,
            [JsonSerializer.SerializeToElement(changed)], [], volumes));
        update = update with { CaptureContainer = null };
        update.Save(root);
        Assert.Throws<UsageException>(() => UninstallInventory.Build(root, current, UninstallMode.Normal,
            UninstallHistory.Load(root, current), [helper], [], volumes));
    }

    /// <summary>Prepare only the supported explicit normal waiver; no accepted destructive command is exposed.</summary>
    private Task<UninstallPlan> Prepare() => preparation.PrepareAsync(root, UninstallOptions.Parse(["--plan", "--without-backup"]));

    /// <summary>Use a structurally valid protected update intent so refusal proves unresolved state rather than malformed JSON.</summary>
    private UpdateReceipt WriteUpdate()
    {
        var source = ReleaseStore.Select(root, config.Release!).Target(config.Project);
        var configured = config with { Installation = Guid.NewGuid(), Backup = new BackupPolicy { Destination = "/backups",
            Payload = Path.Combine(config.Bundle, "wayfarer-recovery"), PayloadSha256 = source.PayloadFingerprint,
            Generation = new string('a', 64), Source = source, Uploads = "uploads", Ring = "data-protection" } };
        var plan = new UpdatePlan(Guid.NewGuid(), root, configured, configured, config.Release!,
            new ReleaseSourceBoundary("1.9.22", config.Release!.Fingerprint, source.ExpectedMigrations.Last(), true, false, "none", "none"), [],
            JsonSerializer.Serialize(configured), configured.EnvironmentFile(root), new string('a', 64), new UpdateCapacity(0, 0, 0, 0, 0, 0))
        { TargetMigrations = source.ExpectedMigrations };
        var receipt = new UpdateReceipt { Plan = plan, PlanHash = plan.Hash() };
        receipt.Save(root);
        return receipt;
    }

    /// <summary>Use a structurally valid protected restore intent and the existing unresolved-restore validator.</summary>
    private void WriteRestore()
    {
        var target = config with { Installation = Guid.NewGuid() };
        var plan = new RestorePlan(Guid.NewGuid(), root, target, target.Installation, Guid.NewGuid(), new string('a', 64),
            DateTimeOffset.UnixEpoch, "quiesced", "bundle", "capture", "restore", null, Guid.NewGuid().ToString("N"), false, true, false)
        { OperatorOwner = target.Release };
        new RestoreReceipt { Plan = plan, PlanHash = plan.Hash() }.Save(root);
    }

    /// <summary>Existing process boundary returns inspection facts only and rejects every mutation verb.</summary>
    private sealed class ReadOnlyRunner(Deployment config, string dockerRoot) : IProcessRunner
    {
        internal string DockerRoot { get; } = dockerRoot;
        internal List<string[]> Commands { get; } = [];
        internal bool Replaced { get; set; }
        internal bool FailInspection { get; set; }

        /// <summary>Simulate a completed installation with owned data volumes and absent runtime.</summary>
        public Task<ProcessResult> RunAsync(string[] args, string? input, CancellationToken cancellation, Action<string>? lineOutput = null)
        {
            Commands.Add(args);
            string output;
            if (args is ["info", "--format", "{{.DockerRootDir}}"]) output = DockerRoot;
            else if (args[0] == "info") output = NativePlatform.Current;
            else if (args is ["compose", "version", "--short"]) output = "2.24.4";
            else if (args[0] == "ps" || args is ["network", "ls", ..]) output = "";
            else if (args is ["volume", "ls", ..]) output = string.Join('\n', UninstallInventory.Roles(config).Select(role => ActiveStorage.Volume(config, role)));
            else if (args is ["volume", "inspect", var name])
            {
                if (FailInspection) return Task.FromResult(new ProcessResult(1, "unavailable"));
                var role = UninstallInventory.Roles(config).Single(role => ActiveStorage.Volume(config, role) == name);
                var volume = UninstallPlanningTests.Volume(config, role);
                var facts = volume.Deserialize<Dictionary<string, JsonElement>>()!;
                facts["Mountpoint"] = JsonSerializer.SerializeToElement(Path.Combine(DockerRoot, "volumes", name, "_data"));
                if (Replaced) facts["CreatedAt"] = JsonSerializer.SerializeToElement("replacement");
                output = JsonSerializer.Serialize(new[] { facts });
            }
            else throw new InvalidOperationException("Unexpected or destructive Docker call: " + string.Join(' ', args));
            return Task.FromResult(new ProcessResult(0, output));
        }
    }

    /// <summary>Only this test's generated fixture tree and reused release fixture are reclaimed.</summary>
    public void Dispose()
    {
        Directory.Delete(root, true);
        Directory.Delete(runner.DockerRoot, true);
        releases.Dispose();
    }
}
