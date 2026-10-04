using System.Text.Json;
using Wayfarer.Tests.Infrastructure;
using WayfarerCtl;
using WayfarerRecovery;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Backup authority uses protected disposable files and recorded process calls, never a host installation.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[Trait("Category", "RequiresRoot")]
public sealed class BackupSourceAuthorityTests : IDisposable
{
    private readonly TestDirectory fixture = new();
    private readonly TestDirectory destination = new();
    private readonly ReleaseBundleTests releases = new();
    private string Root => fixture.Path;

    /// <summary>Retained release placement requires a root-owned private installation parent.</summary>
    public BackupSourceAuthorityTests()
    {
        ProtectedFiles.RequireRoot();
        File.SetUnixFileMode(Root, ProtectedFiles.PrivateDirectory);
    }

    /// <summary>New configuration derives status and worker identity from the selected release's actual capture pair.</summary>
    [Theory]
    [InlineData(true, "released")]
    [InlineData(false, "candidate")]
    [InlineData(null, "candidate")]
    public async Task ConfigurationPersistsRetainedCaptureAuthority(bool? stable, string status)
    {
        var bundle = releases.RetainOperator(Root, "1.9.21", runningOperator: false, stable.GetValueOrDefault());
        var config = Install(bundle);
        if (stable is null)
        {
            config = config with { Schema = 2, Release = null };
            File.WriteAllText(Path.Combine(Root, "installation.json"), JsonSerializer.Serialize(config));
        }
        var runner = new BackupRunner(config, bundle);
        using var operation = Setup.Lock(Root);
        var next = await new BackupConfiguration(runner).ConfigureAsync(Root, config,
            ["--destination", destination.Path, "--payload", Path.Combine(bundle.Directory, "wayfarer-recovery")], default);
        var selected = Deployment.Load(Root);
        Assert.Equal(status, selected.Backup!.Source.ReleaseStatus);
        Assert.Equal(stable is null ? typeof(WorkerConfiguration).Assembly.GetName().Version!.ToString() :
            bundle.Manifest.Application.WorkerVersion, selected.Backup.Source.WorkerVersion);
        Assert.Equal(next.Backup!.Generation, selected.Backup.Generation);
        if (stable is not null) bundle.Corroborate(selected.Backup.Source);
        BackupCompose.Check(Root, selected);
    }

    /// <summary>The validated-bundle seam repairs only status, retains all other inputs and recreates only a previously running scheduler.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task RepairPublishesCoherentGenerationAndPreservesExistingBytes(bool enabled, bool running)
    {
        var (config, bundle) = await KnownState(enabled);
        ProtectedFiles.Create(Path.Combine(destination.Path, "selected.tar"), "selected archive bytes", 1654);
        ProtectedFiles.Create(Path.Combine(destination.Path, "selected.tar.sha256"), "selected sidecar bytes", 1654);
        var retained = Directory.EnumerateFiles(config.Bundle, "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "secrets")))
            .Concat(Directory.EnumerateFiles(destination.Path)).Append(Path.Combine(Root, "deployment.env"))
            .ToDictionary(path => path, File.ReadAllBytes);
        var oldWorker = File.ReadAllBytes(Path.Combine(BackupCompose.DirectoryPath(Root, config.Backup!), "worker.json"));
        var runner = new BackupRunner(config, bundle, Root, running);
        var repair = new BackupSourceRepair(runner);
        Assert.Throws<IOException>(() => bundle.Corroborate(config.Backup!.Source));
        using var operation = Setup.Lock(Root);
        Assert.True(await repair.RepairAsync(Root, config, bundle, default));
        var next = JsonSerializer.Deserialize<Deployment>(File.ReadAllText(Path.Combine(Root, "installation.json")))!;
        Assert.Equal("released", next.Backup!.Source.ReleaseStatus);
        Assert.NotEqual(config.Backup!.Generation, next.Backup.Generation);
        Assert.Equal(JsonSerializer.Serialize(config with { Backup = config.Backup with { Source = next.Backup.Source,
            Generation = next.Backup.Generation } }), JsonSerializer.Serialize(next));
        bundle.Corroborate(next.Backup.Source);
        BackupCompose.Check(Root, next);
        var worker = WorkerConfiguration.Load(Path.Combine(BackupCompose.DirectoryPath(Root, next.Backup), "worker.json"));
        Assert.Equal("released", worker.Source.ReleaseStatus);
        Assert.Equal(next.Backup.Generation, worker.Generation);
        Assert.Equal(oldWorker, File.ReadAllBytes(Path.Combine(BackupCompose.DirectoryPath(Root, config.Backup), "worker.json")));
        foreach (var (path, bytes) in retained) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(running, runner.Calls.Any(args => args.Contains("up")));
        Assert.False(File.Exists(Path.Combine(Root, "backup-transition.json")));
        runner.Calls.Clear();
        var pointer = File.ReadAllBytes(Path.Combine(Root, "installation.json"));
        Assert.False(await repair.RepairAsync(Root, next, bundle, default));
        Assert.Empty(runner.Calls);
        Assert.Equal(pointer, File.ReadAllBytes(Path.Combine(Root, "installation.json")));
    }

    /// <summary>Contradictory source/runtime facts never gain migration authority or change the installation pointer.</summary>
    [Theory]
    [InlineData("release")]
    [InlineData("candidate-release")]
    [InlineData("later-release")]
    [InlineData("owner")]
    [InlineData("version")]
    [InlineData("revision")]
    [InlineData("image")]
    [InlineData("database")]
    [InlineData("project")]
    [InlineData("bundle")]
    [InlineData("payload")]
    [InlineData("worker")]
    [InlineData("migrations")]
    [InlineData("quartz")]
    [InlineData("schema")]
    [InlineData("legacy-support")]
    [InlineData("layout")]
    [InlineData("destination")]
    [InlineData("generated-worker")]
    [InlineData("payload-bytes")]
    [InlineData("unconfigured")]
    public async Task RepairRefusesEveryOtherContradiction(string fact)
    {
        var (config, bundle) = await KnownState();
        var source = config.Backup!.Source;
        source = fact switch
        {
            "version" => source with { ApplicationVersion = "1.9.22" },
            "revision" => source with { SourceRevision = new string('f', 40) },
            "image" => source with { ApplicationImage = "ghcr.io/stef-k/wayfarer@sha256:" + new string('f', 64) },
            "database" => source with { DatabaseImage = "ghcr.io/stef-k/wayfarer-db@sha256:" + new string('f', 64) },
            "project" => source with { Project = "foreign-project" },
            "bundle" => source with { BundleFingerprint = new string('f', 64) },
            "payload" => source with { PayloadFingerprint = new string('f', 64) },
            "worker" => source with { WorkerVersion = "1.9.22.0" },
            "migrations" => source with { ExpectedMigrations = ["20260101000000_Foreign"] },
            "quartz" => source with { QuartzCompatibilityContract = "foreign-contract" },
            "schema" => source with { ConfigurationSchema = 2, QuartzIdentity = "legacy", QuartzCompatibilityContract = null,
                QuartzSnapshotFingerprint = null, SupportedLegacySourceSchemas = null },
            "legacy-support" => source with { SupportedLegacySourceSchemas = [] },
            _ => source
        };
        config = config with { Backup = config.Backup with { Source = source } };
        if (fact == "release") bundle = bundle with { Fingerprint = new string('f', 64) };
        if (fact == "candidate-release") bundle = bundle with { Manifest = bundle.Manifest with { Status = "candidate", Tag = null } };
        if (fact == "later-release") bundle = bundle with { Manifest = bundle.Manifest with { Version = "1.9.22", Tag = "v1.9.22" } };
        if (fact == "owner") config = config with { Release = config.Release! with { OperatorSha256 = new string('f', 64) } };
        if (fact == "layout") config = config with { Backup = config.Backup with { Ring = "foreign-ring" } };
        if (fact == "destination") config = config with { Backup = config.Backup with { Inode = 1 } };
        if (fact == "generated-worker") File.AppendAllText(Path.Combine(BackupCompose.DirectoryPath(Root, config.Backup), "worker.json"), " ");
        if (fact == "payload-bytes") File.AppendAllText(config.Backup.Payload, "foreign bytes");
        if (fact == "unconfigured") config = config with { Backup = null };
        var pointer = File.ReadAllBytes(Path.Combine(Root, "installation.json"));
        var runner = new BackupRunner(config, bundle);
        using var operation = Setup.Lock(Root);
        await Assert.ThrowsAnyAsync<Exception>(() => new BackupSourceRepair(runner).RepairAsync(Root, config, bundle, default));
        Assert.Empty(runner.Calls);
        Assert.Equal(pointer, File.ReadAllBytes(Path.Combine(Root, "installation.json")));
    }

    /// <summary>Receipts, delegated recovery, interrupted publication and derived lifecycle exclusion all precede repair.</summary>
    [Theory]
    [InlineData("restore")]
    [InlineData("update")]
    [InlineData("backup-transition.json")]
    [InlineData("installation.backup-next")]
    [InlineData("recovery-control/host-operation.json")]
    [InlineData("recovery-control/restore-in-progress")]
    [InlineData("recovery-control/update-in-progress")]
    public async Task RepairRefusesUnresolvedOperations(string state)
    {
        var (config, bundle) = await KnownState();
        if (state == "restore")
        {
            var plan = new RestorePlan(Guid.NewGuid(), Root, config, config.Installation, Guid.NewGuid(), new string('a', 64),
                DateTimeOffset.UtcNow, "quiesced", config.Backup!.Source.BundleFingerprint, config.Backup.PayloadSha256,
                config.Backup.PayloadSha256, null, Guid.NewGuid().ToString("N"), false, false, false) { OperatorOwner = config.Release };
            new RestoreReceipt { Plan = plan, PlanHash = plan.Hash(), Phase = RestorePhase.Authorized }.Save(Root);
        }
        else if (state == "update")
        {
            var plan = new UpdatePlan(Guid.NewGuid(), Root, config, config, config.Release!,
                new ReleaseSourceBoundary("1.9.21", bundle.Fingerprint, bundle.Manifest.Application.TerminalMigration, true, false, "manual-recovery", ""),
                [], JsonSerializer.Serialize(config), config.EnvironmentFile(Root), ProtectedFiles.SecretsFingerprint(Root), new UpdateCapacity(1, 2, 2, 2, 0, 0))
                { TargetMigrations = bundle.Manifest.Application.Migrations };
            new UpdateReceipt { Plan = plan, PlanHash = plan.Hash(), Phase = UpdatePhase.Authorized }.Save(Root);
        }
        else ProtectedFiles.Create(Path.Combine(Root, state), "unresolved");
        var pointer = File.ReadAllBytes(Path.Combine(Root, "installation.json"));
        var runner = new BackupRunner(config, bundle);
        using var operation = Setup.Lock(Root);
        await Assert.ThrowsAnyAsync<Exception>(() => new BackupSourceRepair(runner).RepairAsync(Root, config, bundle, default));
        Assert.Empty(runner.Calls);
        Assert.Equal(pointer, File.ReadAllBytes(Path.Combine(Root, "installation.json")));
    }

    /// <summary>A stable version label and internally valid synthetic inventory cannot satisfy the public-byte pin at the command boundary.</summary>
    [Fact]
    public async Task CommandRefusesForeignStableBundleBeforeDocker()
    {
        var (config, bundle) = await KnownState(modelPublicIdentity: false);
        var executable = Path.Combine(Root, "repair-operator");
        File.Copy(Environment.ProcessPath!, executable);
        File.SetUnixFileMode(executable, (UnixFileMode)ReleaseContract.Mode("wayfarerctl"));
        var runner = new BackupRunner(config, bundle);
        var error = await Assert.ThrowsAsync<IOException>(() => new BackupSourceRepair(runner) { ExecutablePath = executable }.RunAsync(Root, default));
        Assert.Equal("Repair accepts only the immutable public v1.9.21 release.", error.Message);
        Assert.Empty(runner.Calls);
        Assert.Throws<UsageException>(() => ReleaseCommands.Validate(["repair-backup-source-v1.9.21", "--skip-owner"]));
    }

    /// <summary>Model already validated public identity at the narrow generation seam; RunAsync separately revalidates actual retained bytes.</summary>
    private async Task<(Deployment Config, ReleaseBundle Bundle)> KnownState(bool enabled = true, bool modelPublicIdentity = true)
    {
        var original = releases.RetainOperator(Root, "1.9.21", runningOperator: false, stable: true);
        var manifest = original.Manifest with { SourceRevision = "709a39ca7876fb4a08ce3090d79c4410efce09d8" };
        File.WriteAllText(Path.Combine(original.Directory, "release.json"), JsonSerializer.Serialize(manifest));
        var bundle = ReleaseBundle.Validate(original.Directory, installed: true);
        var config = Install(bundle);
        using var operation = Setup.Lock(Root);
        var configured = await new BackupConfiguration(new BackupRunner(config, bundle)).ConfigureAsync(Root, config,
            ["--destination", destination.Path, "--payload", Path.Combine(bundle.Directory, "wayfarer-recovery")], default);
        if (modelPublicIdentity) bundle = bundle with { Fingerprint = BackupSourceRepair.PublicFingerprint(NativePlatform.Current) };
        var stale = configured with { Release = ReleaseAuthority.From(bundle), Backup = configured.Backup! with
        {
            Enabled = enabled, Generation = new string('e', 64), Source = configured.Backup.Source with { ReleaseStatus = "candidate" }
        } };
        File.WriteAllText(Path.Combine(Root, "installation.json"), JsonSerializer.Serialize(stale));
        BackupGeneration.Stage(Root, stale);
        return (stale, bundle);
    }

    /// <summary>Provision only the existing completed-installation contract in the test's own root.</summary>
    private Deployment Install(ReleaseBundle bundle)
    {
        File.SetUnixFileMode(Root, ProtectedFiles.PrivateDirectory);
        var config = new Deployment { Schema = 4, Installation = Guid.NewGuid(), Release = ReleaseAuthority.From(bundle),
            Bundle = bundle.Directory, Hostname = "wayfarer.example.org", Mode = "external", Platform = NativePlatform.Current,
            AppDigest = bundle.Manifest.Images.PlatformDigest, DbDigest = bundle.Manifest.Images.DatabaseDigest };
        ProtectedFiles.Create(Path.Combine(Root, "installation.json"), JsonSerializer.Serialize(config));
        ProtectedFiles.Create(Path.Combine(Root, "deployment.env"), config.EnvironmentFile(Root));
        ProtectedFiles.CreateSecrets(Root);
        ProtectedFiles.Create(Path.Combine(Root, "setup-complete"), "");
        return config;
    }

    /// <summary>Supply independent application inspection and Compose facts through the shipped process boundary.</summary>
    private sealed class BackupRunner(Deployment config, ReleaseBundle bundle, string? root = null, bool running = false) : IProcessRunner
    {
        public List<string[]> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string[] args, string? input, CancellationToken cancellation, Action<string>? lineOutput = null)
        {
            Calls.Add(args);
            var output = args[0] switch
            {
                "info" => NativePlatform.Current,
                "wait" => "0",
                "image" => bundle.Manifest.SourceRevision,
                "inspect" => JsonSerializer.Serialize(new[] { new { Config = new
                {
                    Image = "ghcr.io/stef-k/wayfarer-db@" + config.DbDigest, Labels = new Dictionary<string, string>
                    {
                        ["com.docker.compose.project"] = config.Project, ["com.docker.compose.service"] = "backup-scheduler",
                        ["com.docker.compose.project.working_dir"] = config.Bundle,
                        ["com.docker.compose.project.config_files"] = Path.Combine(config.Bundle, "compose.yaml") + "," +
                            Path.Combine(config.Bundle, "external.yaml") + "," + Path.Combine(BackupCompose.DirectoryPath(root!, config.Backup!), "compose.json")
                    }
                }, State = new { Running = running } } }),
                _ when args is ["compose", "version", "--short"] => "2.24.4",
                _ when args.Contains("ps") => running ? "scheduler-id" : "",
                _ when args.Contains("--format") => JsonSerializer.Serialize(new { services = new
                {
                    wayfarer = new { image = "ghcr.io/stef-k/wayfarer@" + config.AppDigest, platform = config.RuntimePlatform },
                    db = new { image = "ghcr.io/stef-k/wayfarer-db@" + config.DbDigest, platform = config.RuntimePlatform }
                } }),
                _ when args.Contains("/inspection/WayfarerRecoverySource.dll") => JsonSerializer.Serialize(new
                {
                    Schema = 2, ApplicationName = "Wayfarer", ApplicationVersion = bundle.Manifest.Application.CompiledVersion,
                    ExpectedMigrations = bundle.Manifest.Application.Migrations,
                    QuartzCompatibilityContract = bundle.Manifest.Application.QuartzCompatibilityContract,
                    QuartzSnapshotFingerprint = new string('a', 32), Uploads = "uploads", Ring = "data-protection"
                }),
                _ => ""
            };
            return Task.FromResult(new ProcessResult(0, output));
        }
    }

    /// <summary>Remove only the three fixture-owned trees, including consumer-owned destination files.</summary>
    public void Dispose()
    {
        fixture.Dispose();
        destination.Dispose();
        releases.Dispose();
    }
}
