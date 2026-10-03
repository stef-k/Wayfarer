using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Wayfarer.Tests.Infrastructure;
using WayfarerCtl;
using WayfarerRecovery;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Recovery coordination exercises real protected Linux files in one owned temporary installation.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[Trait("Category", "RequiresRoot")]
public sealed class RecoveryCoordinationTests : IDisposable
{
    private readonly TestDirectory fixture = new();
    private readonly Deployment config;
    private string Root => fixture.Path;
    private string Reservation => Path.Combine(Root, "recovery-control/host-operation.json");

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string source, string destination);

    /// <summary>Provision the existing control contract without selecting a host installation or Docker resource.</summary>
    public RecoveryCoordinationTests()
    {
        ProtectedFiles.RequireRoot();
        File.SetUnixFileMode(Root, ProtectedFiles.PrivateDirectory);
        config = new Deployment { Schema = 2, Installation = Guid.NewGuid(), Bundle = Path.Combine(Root, "bundle"),
            Hostname = "wayfarer.example.org", Mode = "external", AppDigest = "sha256:" + new string('a', 64) };
        foreach (var name in new[] { "compose.yaml", "external.yaml", "caddy/Caddyfile", "db/20-wayfarer.sh", "config/deployment.env.example" })
        {
            var path = Path.Combine(config.Bundle, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, name);
        }
        ProtectedFiles.Create(Path.Combine(Root, "installation.json"), JsonSerializer.Serialize(config));
        ProtectedFiles.Create(Path.Combine(Root, "deployment.env"), config.EnvironmentFile(Root));
        ProtectedFiles.CreateSecrets(Root);
        ProtectedFiles.Create(Path.Combine(Root, "setup-complete"), "");
        BackupConfiguration.ProvisionControl(Root, bootstrap: true);
    }

    /// <summary>Actual staged-pointer interruption and later recreation failure both replay under the same unresolved receipt.</summary>
    [Fact]
    public async Task CanonicalRestoreAbortReplaysInterruptedPublication()
    {
        Assert.Equal(config, Deployment.Load(Root));
        Assert.True(InstallationCompletion.IsComplete(Root));
        var receipt = PendingPointerRestore();
        var pointer = Path.Combine(Root, "installation.json");
        var staged = pointer + ".abort";
        var originalReceipt = File.ReadAllBytes(RestoreReceipt.PathFor(Root));
        var args = new[] { "--abort", receipt.Plan.Operation.ToString("D") };
        var runner = new AbortRunner();
        var interrupted = new RestoreCommands(runner, new RecordingTerminal())
        {
            AbortPointerStaged = () => throw new IOException("Interrupted before pointer rename.")
        };
        Assert.Equal(1, await interrupted.RunAsync(Root, args, default));
        ProtectedFiles.Check(staged, 0);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(receipt.OldConfiguration!), File.ReadAllBytes(staged));
        Assert.Equal(receipt.NewConfiguration, File.ReadAllText(pointer));
        Assert.Equal(originalReceipt, File.ReadAllBytes(RestoreReceipt.PathFor(Root)));
        Assert.False(RestoreReceipt.Load(Root)!.WritesPossible);

        runner.FailRecreation = true;
        var retry = new RestoreCommands(runner, new RecordingTerminal());
        Assert.Equal(1, await retry.RunAsync(Root, args, default));
        Assert.Equal(receipt.OldConfiguration, File.ReadAllText(pointer));
        Assert.False(File.Exists(staged));
        Assert.Equal(originalReceipt, File.ReadAllBytes(RestoreReceipt.PathFor(Root)));
        runner.FailRecreation = false;
        Assert.Equal(0, await retry.RunAsync(Root, args, default));
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(receipt.OldConfiguration!), File.ReadAllBytes(pointer));
        Assert.False(File.Exists(staged));
        var resolved = RestoreReceipt.Load(Root)!;
        Assert.Equal(RestorePhase.Aborted, resolved.Phase);
        Assert.False(resolved.WritesPossible);
        Assert.Equal(receipt.PlanHash, resolved.PlanHash);
        Assert.All(runner.Calls.Where(call => call.Contains("create")), call =>
            Assert.Equal(config.Compose(Root, "create", "--force-recreate", "--pull", "never", "db", "wayfarer"), call));
        Assert.DoesNotContain(runner.Calls, call => call.Contains("up") || call.Contains("start"));
    }

    /// <summary>Select a validated candidate while retaining exact old/new pointer bytes in protected pre-writer intent.</summary>
    private RestoreReceipt PendingPointerRestore(RestorePhase phase = RestorePhase.ActivationIntent, Deployment? previous = null)
    {
        var old = previous ?? config;
        var plan = new RestorePlan(Guid.NewGuid(), Root, old, old.Installation, Guid.NewGuid(),
            new string('a', 64), DateTimeOffset.UnixEpoch, "quiesced", "bundle", "capture", "restore",
            old.StorageGeneration, Guid.NewGuid().ToString("N"), false, true, false);
        var candidate = RestoreCandidate.Configuration(plan);
        RestoreActivation.Commit(Root, candidate);
        var receipt = new RestoreReceipt { Plan = plan, PlanHash = plan.Hash(), Phase = phase,
            OldConfiguration = JsonSerializer.Serialize(old), NewConfiguration = JsonSerializer.Serialize(candidate) };
        receipt.Save(Root);
        return receipt;
    }

    /// <summary>Only Docker preflight and stopped canonical service recreation belong to the focused abort path.</summary>
    private sealed class AbortRunner : IProcessRunner
    {
        public List<string[]> Calls { get; } = [];
        public bool FailRecreation { get; set; }
        public Task<ProcessResult> RunAsync(string[] args, string? input, CancellationToken cancellation, Action<string>? lineOutput = null)
        {
            Calls.Add(args);
            var output = args[0] == "info" ? NativePlatform.Current : args is ["compose", "version", "--short"] ? "2.24.4" : "";
            return Task.FromResult(new ProcessResult(FailRecreation && args.Contains("create") ? 1 : 0, output));
        }
    }

    /// <summary>A restore committed during Docker preflight must remain selected when backup policy is subsequently disabled.</summary>
    [Fact]
    public async Task OrdinaryBackupConfigurationUsesStorageSelectedAfterPreflight()
    {
        var payload = Path.Combine(config.Bundle, "wayfarer-recovery");
        File.WriteAllText(payload, "worker");
        File.SetUnixFileMode(payload, (UnixFileMode)ReleaseContract.Mode("wayfarer-recovery"));
        var inspector = Path.Combine(config.Bundle, "WayfarerRecoverySource.dll");
        File.WriteAllText(inspector, "inspection");
        File.SetUnixFileMode(inspector, (UnixFileMode)ReleaseContract.Mode("WayfarerRecoverySource.dll"));
        var fingerprint = BackupPolicy.Fingerprint(payload);
        var policy = Update().Plan.Current.Backup! with { PayloadSha256 = fingerprint, Source =
            RecoveryCompatibilityTests.Source(3) with { Project = config.Project, Platform = NativePlatform.Current,
                ApplicationImage = "ghcr.io/stef-k/wayfarer@" + config.AppDigest,
                DatabaseImage = "ghcr.io/stef-k/wayfarer-db@" + config.DbDigest,
                BundleFingerprint = BackupConfiguration.BundleFingerprint(config), PayloadFingerprint = fingerprint } };
        var previous = config with { Schema = 3, Backup = policy, StorageGeneration = Guid.NewGuid().ToString("N") };
        RestoreActivation.Commit(Root, previous);
        BackupGeneration.Stage(Root, previous);
        var runner = new PausedPreflightRunner();
        var terminal = new RecordingTerminal();
        var command = new Cli(runner, terminal).RunAsync(["--deployment-root", Root, "backup", "configure", "--disable"]);
        var current = previous with { StorageGeneration = Guid.NewGuid().ToString("N"),
            Backup = policy with { Generation = new string('c', 64), Retention = 11 } };
        try
        {
            await runner.Paused.WaitAsync(TimeSpan.FromSeconds(10));
            using var operation = Setup.Lock(Root);
            RestoreActivation.Commit(Root, current);
            BackupGeneration.Stage(Root, current);
        }
        finally { runner.Continue.TrySetResult(); await command.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.Equal(0, await command.WaitAsync(TimeSpan.FromSeconds(10)));
        var selected = Deployment.Load(Root);
        Assert.Equal(current.StorageGeneration, selected.StorageGeneration);
        Assert.Equal(current.Release, selected.Release);
        Assert.False(selected.Backup!.Enabled);
        Assert.Equal(current.Backup.Retention, selected.Backup.Retention);
        Assert.Equal(JsonSerializer.Serialize(current), File.ReadAllText(Path.Combine(Root, "backup-previous.json")));
        BackupCompose.Check(Root, selected);
        Assert.All(runner.Calls.Where(args => args.Contains("--project-name")), args =>
            Assert.Contains(ActiveStorage.OverlayPath(Root, current), args));
    }

    /// <summary>An already-running ordinary operator cannot use a release selected by another host owner during preflight.</summary>
    [Fact]
    public async Task OrdinaryOperatorRefusesReleaseSelectedAfterPreflight()
    {
        using var releases = new ReleaseBundleTests();
        var source = releases.RetainOperator(Root, "1.9.21", runningOperator: true);
        var previous = config with { Schema = 4, Release = ReleaseAuthority.From(source), Bundle = source.Directory,
            AppDigest = source.Manifest.Images.PlatformDigest, DbDigest = source.Manifest.Images.DatabaseDigest,
            StorageGeneration = Guid.NewGuid().ToString("N") };
        File.WriteAllText(Path.Combine(Root, "deployment.env"), previous.EnvironmentFile(Root));
        RestoreActivation.Commit(Root, previous);
        var executable = Path.Combine(source.Directory, "wayfarerctl");
        Assert.Equal(previous.Release, ReleaseDispatch.CurrentOwner(Root, Deployment.Load(Root), executable));
        var target = releases.RetainOperator(Root, "1.9.22", runningOperator: false);
        var current = previous with { Release = ReleaseAuthority.From(target), Bundle = target.Directory };
        var runner = new PausedPreflightRunner();
        var terminal = new RecordingTerminal();
        var command = new Cli(runner, terminal) { ExecutablePath = executable }.RunAsync(["--deployment-root", Root, "stop"]);
        try
        {
            await runner.Paused.WaitAsync(TimeSpan.FromSeconds(10));
            using var operation = Setup.Lock(Root);
            RestoreActivation.Commit(Root, current);
        }
        finally { runner.Continue.TrySetResult(); await command.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.Equal(2, await command.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("dispatch", terminal.Errors);
        Assert.Equal(2, runner.Calls.Count); // Only deployment-independent Docker info/version checks may run.
        Assert.Equal(current, Deployment.Load(Root));
        Assert.Equal(target.Fingerprint, ReleaseDispatch.Select(Root, resume: false).Fingerprint);
        // Recovery still selects and verifies the original owner after active release selection changes.
        var restore = TerminalRestore(accepted: false);
        var plan = restore.Plan with { Target = previous, OperatorOwner = previous.Release };
        (restore with { Plan = plan, PlanHash = plan.Hash() }).Save(Root);
        Assert.Equal(source.Fingerprint, ReleaseDispatch.Select(Root, resume: true).Fingerprint);
        ReleaseDispatch.RequireExecutable(ReleaseDispatch.Select(Root, resume: true), executable);
    }

    /// <summary>The actual reservation writer's root:1654/0640 output reaches exact stop/wait/removal and durable cleanup.</summary>
    [Fact]
    public async Task GeneratedDelegatedCaptureCanBeReconciled()
    {
        var receipt = Update();
        var reservation = Reserve(receipt);
        var runner = new CaptureRunner(Root, receipt, reservation.Token);
        await ReconcileCapture(receipt, runner);
        Assert.Equal(new[] { "ps", "inspect", "stop", "wait", "rm" }, runner.Calls);
        Assert.False(File.Exists(Reservation));
    }

    /// <summary>Foreign permission, link and parent states remain untouched before Docker can be consulted.</summary>
    [Theory]
    [InlineData("owner")]
    [InlineData("group")]
    [InlineData("mode")]
    [InlineData("hard-link")]
    [InlineData("symbolic-link")]
    [InlineData("parent")]
    public async Task UnsafeDelegatedCaptureIsRefused(string state)
    {
        var receipt = Update();
        var reservation = Reserve(receipt);
        if (state is "owner" or "group")
        {
            File.Delete(Reservation);
            ProtectedFiles.Create(Reservation, JsonSerializer.Serialize(reservation), state == "owner" ? 1654u : 0,
                state == "owner" ? 1654u : 0);
        }
        else if (state == "mode") File.SetUnixFileMode(Reservation, ProtectedFiles.PrivateFile);
        else if (state == "hard-link") Assert.Equal(0, link(Reservation, Reservation + ".alias"));
        else if (state == "symbolic-link")
        {
            File.Move(Reservation, Reservation + ".owned");
            File.CreateSymbolicLink(Reservation, Reservation + ".owned");
        }
        else File.SetUnixFileMode(Path.GetDirectoryName(Reservation)!, (UnixFileMode)509); // Unsafe 0775 parent.
        var original = File.ReadAllBytes(Reservation);
        var runner = new CaptureRunner(Root, receipt, reservation.Token);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => ReconcileCapture(receipt, runner));
            Assert.Empty(runner.Calls);
            Assert.Equal(original, File.ReadAllBytes(Reservation));
        }
        finally
        {
            if (state == "symbolic-link") File.Delete(Reservation);
        }
    }

    /// <summary>Schema, token, hold and phase facts cannot grant another operation's capture authority.</summary>
    [Theory]
    [InlineData("token")]
    [InlineData("container")]
    [InlineData("hold")]
    [InlineData("phase")]
    [InlineData("duplicate")]
    public async Task ForeignDelegatedCaptureIsRefused(string state)
    {
        var receipt = Update();
        var reservation = new HostRecoveryOperation(1, Guid.NewGuid().ToString("N"), receipt.CaptureContainer!, true, UpdateHold: true);
        if (state == "token") reservation = reservation with { Token = "foreign" };
        if (state == "container") reservation = reservation with { Container = "another-backup-" + Guid.NewGuid().ToString("N") };
        if (state == "hold") reservation = reservation with { RestoreHold = true };
        if (state == "phase") receipt = receipt with { Phase = UpdatePhase.Authorized };
        var content = JsonSerializer.Serialize(reservation);
        if (state == "duplicate") content = content.Insert(1, "\"Schema\":2,");
        ProtectedFiles.Create(Reservation, content, 0, 1654);
        var runner = new CaptureRunner(Root, receipt, reservation.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => ReconcileCapture(receipt, runner));
        Assert.Empty(runner.Calls);
        Assert.Equal(content, File.ReadAllText(Reservation));
    }

    /// <summary>A different worker token or an unknown Docker stop/wait result never clears the reservation.</summary>
    [Theory]
    [InlineData("token")]
    [InlineData("stop")]
    [InlineData("wait")]
    public async Task UncertainCaptureWorkerRetainsReservation(string failure)
    {
        var receipt = Update();
        var reservation = Reserve(receipt);
        var original = File.ReadAllBytes(Reservation);
        var runner = new CaptureRunner(Root, receipt, failure == "token" ? Guid.NewGuid().ToString("N") : reservation.Token, failure);
        await Assert.ThrowsAnyAsync<Exception>(() => ReconcileCapture(receipt, runner));
        Assert.DoesNotContain("rm", runner.Calls);
        Assert.Equal(original, File.ReadAllBytes(Reservation));
    }

    /// <summary>Reconstructed terminal-publication residue converges through backup recovery without rewriting lifecycle history.</summary>
    [Theory]
    [InlineData("restore", true)]
    [InlineData("restore", false)]
    [InlineData("update", true)]
    [InlineData("update", false)]
    [InlineData("handoff", true)]
    public async Task ExactTerminalMarkersConvergeThroughBackupRecovery(string kind, bool accepted)
    {
        if (kind == "restore") TerminalRestore(accepted);
        else TerminalUpdate(accepted, kind == "handoff");
        var original = Directory.EnumerateFiles(Root, "*.json", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        using var operation = Setup.Lock(Root);
        var recovery = new BackupConfiguration(new NoProcessRunner());
        Assert.Equal(config, await recovery.ConfigureAsync(Root, config, ["--recover"], default));
        Assert.False(File.Exists(Path.Combine(Root, "recovery-control/restore-in-progress")));
        Assert.False(File.Exists(Path.Combine(Root, "recovery-control/update-in-progress")));
        foreach (var (path, bytes) in original) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(config, await recovery.ConfigureAsync(Root, config, ["--recover"], default));
    }

    /// <summary>Missing/foreign authority, unresolved intent and contradictory completion cannot release worker exclusion.</summary>
    [Theory]
    [InlineData("restore", "unresolved")]
    [InlineData("update", "unresolved")]
    [InlineData("restore", "missing")]
    [InlineData("update", "missing")]
    [InlineData("restore", "foreign")]
    [InlineData("update", "foreign")]
    [InlineData("restore", "completion")]
    [InlineData("update", "completion")]
    [InlineData("restore", "contradictory")]
    [InlineData("update", "handoff")]
    [InlineData("restore", "malformed")]
    [InlineData("update", "unsafe")]
    [InlineData("update", "paired")]
    public async Task UncertainTerminalMarkersRemainUntouched(string kind, string state)
    {
        var marker = Path.Combine(Root, "recovery-control", kind + "-in-progress");
        if (kind == "restore") TerminalRestore(state != "contradictory");
        else TerminalUpdate(true, state == "handoff");
        var receiptPath = kind == "restore" ? RestoreReceipt.PathFor(Root) : UpdateReceipt.PathFor(Root);
        if (state == "unresolved")
        {
            if (kind == "restore") (RestoreReceipt.Load(Root)! with { Phase = RestorePhase.WritesPossible }).Save(Root);
            else (UpdateReceipt.Load(Root)! with { Phase = UpdatePhase.PostflightConfirmed }).Save(Root);
        }
        else if (state == "missing") File.Delete(receiptPath);
        else if (state is "foreign" or "paired") File.WriteAllText(marker, Guid.NewGuid().ToString("D"));
        else if (state == "completion")
        {
            var completion = kind == "restore" ? Path.Combine(Root, "restore-complete") :
                Path.Combine(UpdatePreparation.DirectoryFor(Root, UpdateReceipt.Load(Root)!.Plan.Operation), "completed");
            File.WriteAllText(completion, new string('f', kind == "restore" ? 36 : 64));
        }
        else if (state == "contradictory") ProtectedFiles.Create(Path.Combine(Root, "restore-complete"), RestoreReceipt.Load(Root)!.Plan.Operation.ToString("D"));
        else if (state == "handoff") File.Delete(RestoreReceipt.PathFor(Root));
        else if (state == "malformed") File.WriteAllText(marker, "not-an-operation");
        else File.SetUnixFileMode(marker, ProtectedFiles.PrivateFile | UnixFileMode.GroupRead);
        if (state == "paired") TerminalRestore(true);
        var original = Directory.EnumerateFiles(Path.Combine(Root, "recovery-control"), "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        using var operation = Setup.Lock(Root);
        await Assert.ThrowsAnyAsync<Exception>(() => new BackupConfiguration(new NoProcessRunner())
            .ConfigureAsync(Root, config, ["--recover"], default));
        foreach (var (path, bytes) in original) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    /// <summary>Publish completion before Accepted through production owners, then reconstruct only the surviving derived marker.</summary>
    private RestoreReceipt TerminalRestore(bool accepted, UpdateReceipt? update = null)
    {
        var target = update?.Plan.Current ?? config;
        var plan = new RestorePlan(Guid.NewGuid(), Root, target, target.Installation, update?.RecoveryArchive ?? Guid.NewGuid(),
            update?.RecoverySha256 ?? new string('a', 64), DateTimeOffset.UnixEpoch, "quiesced", "bundle", "capture", "restore",
            null, Guid.NewGuid().ToString("N"), false, true, false)
            { OperatorOwner = target.Release, FromUpdate = update?.Plan.Operation };
        var receipt = new RestoreReceipt { Plan = plan, PlanHash = plan.Hash(), Phase = RestorePhase.WritesPossible };
        receipt.Save(Root);
        if (accepted) InstallationCompletion.RecordRestore(Root, receipt);
        receipt = (receipt with { Phase = accepted ? RestorePhase.WritesPossible : RestorePhase.Authorized })
            .Advance(accepted ? RestorePhase.Accepted : RestorePhase.Aborted);
        receipt.Save(Root);
        ProtectedFiles.Create(Path.Combine(Root, "recovery-control/restore-in-progress"), plan.Operation.ToString("D"));
        return receipt;
    }

    /// <summary>Keep forward acceptance, pre-migration abort and exact accepted-restore ownership distinct.</summary>
    private UpdateReceipt TerminalUpdate(bool accepted, bool handoff = false)
    {
        var receipt = Update();
        if (accepted)
        {
            receipt = receipt with { RecoveryArchive = Guid.NewGuid(), RecoveryName = "held.tar", RecoverySha256 = new string('a', 64),
                MigrationContainer = config.Project + "-update-migrate-" + receipt.Plan.Operation.ToString("N"),
                Phase = handoff ? UpdatePhase.MigrationStarted : UpdatePhase.Accepted };
            if (handoff)
            {
                var restore = TerminalRestore(true, receipt);
                receipt = receipt with { RestoreOperation = restore.Plan.Operation, RestoreAccepted = true };
            }
            else
            {
                receipt = receipt with { NewConfiguration = JsonSerializer.Serialize(receipt.Plan.Target with
                    { Backup = receipt.Plan.Target.Backup! with { Generation = receipt.PlanHash } }) };
                var directory = UpdatePreparation.DirectoryFor(Root, receipt.Plan.Operation);
                Directory.CreateDirectory(directory, ProtectedFiles.PrivateDirectory);
                ProtectedFiles.Create(Path.Combine(directory, "completed"), receipt.PlanHash);
            }
        }
        else receipt = receipt.Advance(UpdatePhase.Aborted);
        receipt.Save(Root);
        ProtectedFiles.Create(Path.Combine(Root, "recovery-control/update-in-progress"), receipt.Plan.Operation.ToString("D"));
        return receipt;
    }

    /// <summary>Backup recovery with no configured scheduler must not invoke any Docker mutation.</summary>
    private sealed class NoProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string[] args, string? input, CancellationToken cancellation, Action<string>? lineOutput = null) =>
            throw new InvalidOperationException("Unexpected Docker operation: " + string.Join(' ', args));
    }

    /// <summary>Pause only the read-only Docker preflight so another real host-lock owner can publish current authority.</summary>
    private sealed class PausedPreflightRunner : IProcessRunner
    {
        private readonly TaskCompletionSource paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Paused => paused.Task;
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string[]> Calls { get; } = [];
        public async Task<ProcessResult> RunAsync(string[] args, string? input, CancellationToken cancellation, Action<string>? lineOutput = null)
        {
            Calls.Add(args);
            if (args[0] == "info")
            {
                paused.TrySetResult();
                await Continue.Task.WaitAsync(cancellation);
                return new ProcessResult(0, NativePlatform.Current);
            }
            return new ProcessResult(0, args is ["compose", "version", "--short"] ? "2.24.4" : "");
        }
    }

    /// <summary>Capture safe CLI refusal guidance without terminal input or child processes.</summary>
    private sealed class RecordingTerminal : ITerminal
    {
        public bool Interactive => false;
        public string Errors { get; private set; } = "";
        public void Write(string message) { }
        public void Error(string message) => Errors += message + "\n";
        public string? Read(string prompt) => throw new InvalidOperationException("Unexpected prompt.");
        public string Password(bool fromStdin) => throw new InvalidOperationException("Unexpected password input.");
    }

    /// <summary>Use the unchanged private coordinator seam so tests never launch the wider update lifecycle.</summary>
    private Task ReconcileCapture(UpdateReceipt receipt, CaptureRunner runner) => (Task)typeof(UpdateCommands)
        .GetMethod("ReconcileCaptureAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(new UpdateCommands(runner, new Terminal()), [Root, receipt, CancellationToken.None])!;

    /// <summary>Generate reservation bytes and ownership through the production writer's exact API.</summary>
    private HostRecoveryOperation Reserve(UpdateReceipt receipt)
    {
        var reservation = new HostRecoveryOperation(1, Guid.NewGuid().ToString("N"), receipt.CaptureContainer!, true, UpdateHold: true);
        ProtectedFiles.Create(Reservation, JsonSerializer.Serialize(reservation), 0, 1654);
        return reservation;
    }

    /// <summary>Construct bounded valid update authority without importing or executing any release.</summary>
    private UpdateReceipt Update()
    {
        var release = new ReleaseAuthority("v1.9.19", new string('a', 64), "1.9.19", new string('b', 64));
        var current = config with { Schema = 4, Release = release, Backup = new BackupPolicy
        {
            Destination = Path.Combine(Root, "destination"), Payload = Path.Combine(config.Bundle, "wayfarer-recovery"),
            PayloadSha256 = new string('a', 64), Generation = new string('b', 64), Uploads = "uploads", Ring = "data-protection",
            Source = RecoveryCompatibilityTests.Source(3) with { Project = config.Project, Platform = NativePlatform.Current }
        } };
        var plan = new UpdatePlan(Guid.NewGuid(), Root, current, current, release,
            new ReleaseSourceBoundary("1.9.19", release.Fingerprint, current.Backup.Source.ExpectedMigrations[^1],
                true, false, "forward-inspection-or-recovery", ""), [], JsonSerializer.Serialize(current),
            current.EnvironmentFile(Root), new string('c', 64), new UpdateCapacity(1, 2, 2, 2, 0, 0))
            { TargetMigrations = current.Backup.Source.ExpectedMigrations };
        return new UpdateReceipt { Plan = plan, PlanHash = plan.Hash(), Phase = UpdatePhase.Fenced,
            CaptureContainer = config.Project + "-backup-" + Guid.NewGuid().ToString("N") };
    }

    /// <summary>Return exact generated Compose identity and record only the focused worker reconciliation sequence.</summary>
    private sealed class CaptureRunner(string root, UpdateReceipt receipt, string token, string? failure = null) : IProcessRunner
    {
        public List<string> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(string[] args, string? input, CancellationToken cancellation, Action<string>? lineOutput = null)
        {
            Calls.Add(args[0]);
            var current = receipt.Plan.Current;
            var output = args[0] switch
            {
                "ps" => receipt.CaptureContainer!,
                "inspect" => JsonSerializer.Serialize(new[] { new
                {
                    Config = new { Image = "ghcr.io/stef-k/wayfarer-db@" + current.DbDigest,
                        Cmd = new[] { "backup", "--host-operation", token }, Labels = new Dictionary<string, string>
                        {
                            ["com.docker.compose.project"] = current.Project,
                            ["com.docker.compose.service"] = "backup-worker",
                            ["com.docker.compose.project.working_dir"] = current.Bundle,
                            ["com.docker.compose.project.config_files"] = Path.Combine(current.Bundle, "compose.yaml") + "," +
                                Path.Combine(current.Bundle, "external.yaml") + "," + Path.Combine(BackupCompose.DirectoryPath(root, current.Backup!), "compose.json")
                        } }, State = new { Status = "running" }
                } }),
                _ => "0"
            };
            return Task.FromResult(new ProcessResult(args[0] == failure ? 1 : 0, output));
        }
    }

    /// <summary>Reclaim only this test's private installation and credentials.</summary>
    public void Dispose() => fixture.Dispose();
}
