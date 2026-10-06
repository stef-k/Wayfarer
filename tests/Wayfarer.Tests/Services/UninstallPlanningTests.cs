using System.Text.Json;
using WayfarerCtl;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Uninstall authorization is deterministic and requires no Docker daemon or protected host fixture.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class UninstallPlanningTests
{
    /// <summary>Each supported planning form keeps the backup choice separate from interactive eligibility.</summary>
    [Theory]
    [InlineData("--plan --backup", UninstallMode.Normal, UninstallBackup.VerifiedQuiesced)]
    [InlineData("--plan --without-backup", UninstallMode.Normal, UninstallBackup.Waived)]
    [InlineData("--purge --plan --backup", UninstallMode.Purge, UninstallBackup.VerifiedQuiesced)]
    [InlineData("--purge --plan --without-backup", UninstallMode.Purge, UninstallBackup.Waived)]
    [InlineData("--plan", UninstallMode.Normal, null)]
    [InlineData("--purge --plan", UninstallMode.Purge, null)]
    public void PlanningGrammar(string text, UninstallMode mode, UninstallBackup? backup)
    {
        var options = UninstallOptions.Parse(text.Split(' '));
        Assert.True(options.Plan);
        Assert.Equal(mode, options.Mode);
        Assert.Equal(backup, options.Backup);
        options.CheckInteraction(true);
        if (backup is null) Assert.Throws<UsageException>(() => options.CheckInteraction(false));
        else options.CheckInteraction(false);
    }

    /// <summary>Acceptance has only one exact hash; no independent destructive choice can accompany it.</summary>
    [Fact]
    public void AcceptanceGrammar()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, UninstallOptions.Parse(["--accept-plan", hash]).Accept);
        foreach (var flag in new[] { "--plan", "--purge", "--backup", "--without-backup" })
            Assert.Throws<UsageException>(() => UninstallOptions.Parse(["--accept-plan", hash, flag]));
        Assert.Throws<UsageException>(() => UninstallOptions.Parse(["--accept-plan", hash, "--accept-plan", hash]));
        Assert.Throws<UsageException>(() => UninstallOptions.Parse(["--accept-plan", new string('A', 64)]));
        Assert.Throws<UsageException>(() => UninstallOptions.Parse(["--accept-plan", new string('a', 63)]));
    }

    /// <summary>Unknown, repeated, conflicting and bypass options never acquire authority.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("--purge")]
    [InlineData("--backup")]
    [InlineData("--plan --backup --without-backup")]
    [InlineData("--plan --plan")]
    [InlineData("--purge --plan --purge")]
    [InlineData("--plan --backup --backup")]
    [InlineData("--plan --without-backup --without-backup")]
    [InlineData("--accept-plan bad")]
    [InlineData("--accept-plan")]
    [InlineData("--plan --unknown")]
    [InlineData("--plan --yes")]
    [InlineData("--plan --force")]
    [InlineData("--plan --delete-backups")]
    [InlineData("--plan --image-prune")]
    public void InvalidGrammar(string text) => Assert.Throws<UsageException>(() =>
        UninstallOptions.Parse(text.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

    /// <summary>One mutation for each authority class proves deterministic serialization binds the complete plan.</summary>
    [Fact]
    public void HashBindsEveryAuthorityClass()
    {
        var plan = Plan();
        Assert.Equal(plan.Hash(), JsonSerializer.Deserialize<UninstallPlan>(JsonSerializer.Serialize(plan))!.Hash());
        var changed = new[]
        {
            plan with { Mode = UninstallMode.Purge }, plan with { Backup = UninstallBackup.VerifiedQuiesced },
            plan with { StartingState = UninstallStartingState.Preserved },
            plan with { Current = plan.Current with { Installation = Guid.NewGuid() } },
            plan with { Current = plan.Current with { Project = "another" } },
            plan with { Current = plan.Current with { StorageGeneration = Guid.NewGuid().ToString("N") } },
            plan with { OperatorOwner = plan.OperatorOwner! with { OperatorSha256 = new string('b', 64) } },
            plan with { Resources = [.. plan.Resources.Skip(1)] },
            plan with { Resources = plan.Resources.Select(r => r.Kind == UninstallResourceKind.Volume
                ? r with { Name = r.Name + "_other" } : r).ToArray() },
            plan with { Resources = plan.Resources.Select(r => r.Kind == UninstallResourceKind.Volume
                ? r with { Action = UninstallAction.Remove } : r).ToArray() },
            plan with { Configuration = Convert.ToBase64String([1, 2, 3]) },
            plan with { Environment = Convert.ToBase64String([4, 5, 6]) },
            plan with { SecretsFingerprint = new string('c', 64) },
            plan with { HistoryFingerprint = new string('d', 64) }
        };
        foreach (var value in changed) Assert.NotEqual(plan.Hash(), value.Hash());
    }

    /// <summary>Completed release setup without backup legitimately binds the persisted empty installation UUID.</summary>
    [Fact]
    public void EmptyInstallationIsValidAndNormalModeRetainsAllVolumes()
    {
        var plan = Plan();
        plan.Validate(plan.Root);
        Assert.Equal(Guid.Empty, plan.Current.Installation);
        Assert.All(plan.Resources.Where(r => r.Kind == UninstallResourceKind.Volume),
            r => Assert.Equal(UninstallAction.Retain, r.Action));
    }

    /// <summary>Plans cannot change root, choose unsafe starting states or erase excluded storage.</summary>
    [Fact]
    public void InvalidPlanAuthorityFailsClosed()
    {
        var plan = Plan();
        Assert.Throws<UsageException>(() => plan.Validate("/other"));
        foreach (var changed in new[] { plan with { Operation = Guid.Empty }, plan with { Root = "/etc/../etc/wayfarer" },
            plan with { StartingState = UninstallStartingState.Preserved }, plan with { Protocol = 2 },
            plan with { SecretsFingerprint = "bad" }, plan with { Configuration = "bad-base64" } })
            Assert.ThrowsAny<Exception>(() => changed.Validate(plan.Root));
        foreach (var kind in new[] { UninstallResourceKind.Volume, UninstallResourceKind.Image })
            Assert.Throws<UsageException>(() => (plan with { Resources = plan.Resources.Select(r =>
                r.Kind == kind ? r with { Action = UninstallAction.Remove } : r).ToArray() }).Validate(plan.Root));
        var backupTarget = new UninstallResource(UninstallResourceKind.BackupDestination, "/backups", null, null,
            null, UninstallAction.Remove, null);
        Assert.Throws<UsageException>(() => (plan with { Resources = [.. plan.Resources, backupTarget] }).Validate(plan.Root));
    }

    /// <summary>Authoritative DB/app storage is required for preservation; purge may freeze a missing resource.</summary>
    [Fact]
    public void MissingStorageCannotBeCalledPreservable()
    {
        var config = Config();
        Assert.Throws<UsageException>(() => UninstallInventory.Build("/etc/wayfarer", config, UninstallMode.Normal,
            new UninstallHistory([], []), [], [], []));
        var purge = UninstallInventory.Build("/etc/wayfarer", config, UninstallMode.Purge,
            new UninstallHistory([], []), [], [], []);
        Assert.All(purge.Where(r => r.Kind == UninstallResourceKind.Volume), r =>
        {
            Assert.Equal(UninstallAction.Remove, r.Action);
            Assert.Null(r.DockerId);
        });
    }

    /// <summary>A plausible labelled generation without protected history is never cleanup authority.</summary>
    [Fact]
    public void ForeignAndUnreceiptedResourcesAreRefused()
    {
        var config = Config();
        foreach (var volume in new[] { Volume(config, "app-data", "_" + Guid.NewGuid().ToString("N")),
            JsonSerializer.SerializeToElement(new { Name = ActiveStorage.Volume(config, "app-data"),
                Labels = new Dictionary<string, string> { ["com.docker.compose.project"] = "foreign" } }) })
            Assert.Throws<UsageException>(() => UninstallInventory.Build("/etc/wayfarer", config, UninstallMode.Purge,
                new UninstallHistory([], []), [], [], [volume]));
    }

    /// <summary>Terminal restore generation intent authorizes concrete volumes, while edited/unresolved receipts do not.</summary>
    [Fact]
    public void PurgeUsesOnlyTerminalProtectedGenerations()
    {
        var config = Config() with { Installation = Guid.NewGuid() };
        var plan = new RestorePlan(Guid.NewGuid(), "/etc/wayfarer", config, config.Installation, Guid.NewGuid(),
            new string('a', 64), DateTimeOffset.UnixEpoch, "quiesced", "bundle", "capture", "restore", null,
            Guid.NewGuid().ToString("N"), false, true, false) { OperatorOwner = config.Release };
        var candidate = RestoreCandidate.Configuration(plan);
        var receipt = new RestoreReceipt { Plan = plan, PlanHash = plan.Hash(), Phase = RestorePhase.Aborted,
            Volumes = [ActiveStorage.Volume(candidate, "db-data"), ActiveStorage.Volume(candidate, "app-data")] };
        var history = new UninstallHistory([], [receipt]);
        var volumes = UninstallInventory.Build(plan.Root, config, UninstallMode.Purge, history, [], [],
            [Volume(candidate, "db-data"), Volume(candidate, "app-data")]);
        Assert.Contains(volumes, r => r.Name == ActiveStorage.Volume(candidate, "db-data") &&
            r.Action == UninstallAction.Remove && r.LifecycleOperation == plan.Operation);
        foreach (var invalid in new[] { receipt with { Phase = RestorePhase.Staging }, receipt with { PlanHash = new string('b', 64) },
            receipt with { Volumes = [config.Project + "_app-data_" + Guid.NewGuid().ToString("N")] },
            receipt with { OldConfiguration = JsonSerializer.Serialize(config with { Project = "foreign" }) } })
            Assert.ThrowsAny<Exception>(() => UninstallInventory.Build(plan.Root, config, UninstallMode.Purge,
                new UninstallHistory([], [invalid]), [], [], []));
    }

    /// <summary>Forward reconciliation may tolerate a missing resource, but refuses replacements and unexpected creations.</summary>
    [Fact]
    public void ReconciliationDistinguishesAbsenceFromReplacement()
    {
        var planned = Plan().Resources.First(r => r.DockerId is not null);
        var absent = planned with { DockerId = null, Evidence = null };
        UninstallInventory.Reconcile(planned, absent, true);
        Assert.Throws<UsageException>(() => UninstallInventory.Reconcile(planned, absent, false));
        Assert.Throws<UsageException>(() => UninstallInventory.Reconcile(planned, planned with { Evidence = "changed" }, true));
        Assert.Throws<UsageException>(() => UninstallInventory.Reconcile(absent, planned, true));
    }

    /// <summary>Acceptance validates exact protected bytes even when semantic configuration values appear equivalent.</summary>
    [Fact]
    public void AuthorityComparisonRejectsEachStaleClass()
    {
        var plan = Plan();
        var configuration = Convert.FromBase64String(plan.Configuration);
        var environment = Convert.FromBase64String(plan.Environment);
        plan.CheckAuthority(plan.Current, configuration, environment, plan.SecretsFingerprint, plan.BundleFingerprint,
            plan.HistoryFingerprint, plan.StartingState);
        Assert.Throws<UsageException>(() => plan.CheckAuthority(plan.Current with { Installation = Guid.NewGuid() }, configuration,
            environment, plan.SecretsFingerprint, plan.BundleFingerprint, plan.HistoryFingerprint, plan.StartingState));
        Assert.Throws<UsageException>(() => plan.CheckAuthority(plan.Current, [.. configuration, 10], environment,
            plan.SecretsFingerprint, plan.BundleFingerprint, plan.HistoryFingerprint, plan.StartingState));
        Assert.Throws<UsageException>(() => plan.CheckAuthority(plan.Current, configuration, [.. environment, 10],
            plan.SecretsFingerprint, plan.BundleFingerprint, plan.HistoryFingerprint, plan.StartingState));
        Assert.Throws<UsageException>(() => plan.CheckAuthority(plan.Current, configuration, environment,
            new string('e', 64), plan.BundleFingerprint, plan.HistoryFingerprint, plan.StartingState));
    }

    /// <summary>Image, Compose input and all shipped durable mounts are required before a canonical container enters the plan.</summary>
    [Fact]
    public void CanonicalContainerOwnershipAndSecretFreeEvidence()
    {
        var config = Config();
        const string root = "/etc/wayfarer";
        var container = Container(config, root);
        var volumes = new[] { Volume(config, "db-data"), Volume(config, "app-data") };
        var owned = UninstallInventory.Build(root, config, UninstallMode.Normal, new UninstallHistory([], []), [container], [], volumes);
        var runtime = Assert.Single(owned, r => r.Kind == UninstallResourceKind.Container && r.DockerId is not null);
        Assert.Equal(new string('f', 64), runtime.DockerId);
        Assert.DoesNotContain("SECRET-BYTES", runtime.Evidence!);
        foreach (var field in new[] { "image", "mount", "input" })
        {
            var facts = container.Deserialize<Dictionary<string, JsonElement>>()!;
            var configuration = facts["Config"].Deserialize<Dictionary<string, JsonElement>>()!;
            if (field == "image") configuration["Image"] = JsonSerializer.SerializeToElement("foreign:latest");
            if (field == "mount") facts["Mounts"] = JsonSerializer.SerializeToElement(Array.Empty<object>());
            if (field == "input")
            {
                var labels = configuration["Labels"].Deserialize<Dictionary<string, string>>()!;
                labels["com.docker.compose.project.config_files"] = "/foreign/compose.yaml";
                configuration["Labels"] = JsonSerializer.SerializeToElement(labels);
            }
            facts["Config"] = JsonSerializer.SerializeToElement(configuration);
            Assert.ThrowsAny<Exception>(() => UninstallInventory.Build(root, config, UninstallMode.Normal,
                new UninstallHistory([], []), [JsonSerializer.SerializeToElement(facts)], [], volumes));
        }
        Assert.Throws<UsageException>(() => UninstallInventory.Build(root, config, UninstallMode.Normal,
            new UninstallHistory([], []), [container, container], [], volumes));
    }

    /// <summary>Docker mount enumeration order is not identity; changing any mount fact still invalidates acceptance.</summary>
    [Fact]
    public void MountEnumerationOrderPreservesEvidenceButReplacementRefuses()
    {
        var container = Container(Config(), "/etc/wayfarer");
        var facts = container.Deserialize<Dictionary<string, JsonElement>>()!;
        var mounts = facts["Mounts"].EnumerateArray().Reverse().ToArray();
        facts["Mounts"] = JsonSerializer.SerializeToElement(mounts);
        var evidence = UninstallInventory.Evidence(UninstallResourceKind.Container, container);
        Assert.Equal(evidence, UninstallInventory.Evidence(UninstallResourceKind.Container, JsonSerializer.SerializeToElement(facts)));
        var changed = mounts[0].Deserialize<Dictionary<string, JsonElement>>()!;
        changed["RW"] = JsonSerializer.SerializeToElement(true);
        mounts[0] = JsonSerializer.SerializeToElement(changed);
        facts["Mounts"] = JsonSerializer.SerializeToElement(mounts);
        var planned = new UninstallResource(UninstallResourceKind.Container, "wayfarer-wayfarer-1", new string('f', 64),
            "wayfarer", null, UninstallAction.Remove, evidence);
        var actual = planned with { Evidence = UninstallInventory.Evidence(UninstallResourceKind.Container, JsonSerializer.SerializeToElement(facts)) };
        Assert.Throws<UsageException>(() => UninstallInventory.Reconcile(planned, actual, false));
    }

    /// <summary>Unreceipted helpers and plausible same-project networks cannot become removal authority.</summary>
    [Fact]
    public void UnreceiptedHelpersAndNetworksBlockInventory()
    {
        var config = Config();
        var root = "/tmp/wayfarer-uninstall-missing-" + Guid.NewGuid().ToString("N");
        var helper = JsonSerializer.SerializeToElement(new { Name = "/" + config.Project + "-restore-unreceipted", Id = new string('f', 64),
            Config = new { Labels = new Dictionary<string, string> { ["wayfarer.restore-helper"] = config.Project },
                Image = "ghcr.io/stef-k/wayfarer@" + config.AppDigest }, State = new { Running = false },
            HostConfig = new { RestartPolicy = new { Name = "no" } }, Mounts = Array.Empty<object>() });
        Assert.Throws<UsageException>(() => UninstallInventory.Build(root, config, UninstallMode.Purge, new UninstallHistory([], []), [helper], [], []));
        var network = JsonSerializer.SerializeToElement(new { Name = config.Project + "-restore-" + Guid.NewGuid().ToString("N"),
            Labels = new Dictionary<string, string> { ["wayfarer.restore-helper"] = config.Project } });
        Assert.Throws<UsageException>(() => UninstallInventory.Build(root, config, UninstallMode.Purge, new UninstallHistory([], []), [], [network], []));
    }

    /// <summary>Backup policy identity and excluded destination are hashed without granting destination deletion.</summary>
    [Fact]
    public void ConfiguredBackupDestinationIsAlwaysExcluded()
    {
        var original = Plan();
        var source = RecoveryCompatibilityTests.Source(3) with { Project = original.Current.Project,
            ApplicationImage = "ghcr.io/stef-k/wayfarer@" + original.Current.AppDigest,
            DatabaseImage = "ghcr.io/stef-k/wayfarer-db@" + original.Current.DbDigest };
        var config = original.Current with { Installation = Guid.NewGuid(), Backup = new BackupPolicy
            { Destination = "/backups", Payload = "/payload/wayfarer-recovery", PayloadSha256 = new string('e', 64),
                Generation = new string('a', 64), Source = source, Uploads = "uploads", Ring = "data-protection" } };
        var resources = UninstallInventory.Build(original.Root, config, UninstallMode.Purge, new UninstallHistory([], []), [], [], []);
        var plan = original with { Mode = UninstallMode.Purge, Current = config,
            Configuration = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(config)), Resources = resources };
        plan.Validate(plan.Root);
        Assert.Equal(UninstallAction.Exclude, Assert.Single(resources, r => r.Kind == UninstallResourceKind.BackupDestination).Action);
        Assert.NotEqual(plan.Hash(), (plan with { Current = config with { Backup = config.Backup with { Generation = new string('b', 64) } } }).Hash());
        Assert.Throws<UsageException>(() => (plan with { Resources = resources.Select(r => r.Kind == UninstallResourceKind.BackupDestination
            ? r with { Action = UninstallAction.Remove } : r).ToArray() }).Validate(plan.Root));
    }

    /// <summary>Only the production service ownership facts are supplied; raw environment and unrelated labels are deliberately secret-bearing.</summary>
    private static JsonElement Container(Deployment config, string root) => JsonSerializer.SerializeToElement(new
    {
        Id = new string('f', 64), Name = "/" + config.Project + "-wayfarer-1", Created = "2026-10-05T00:00:00Z", Image = new string('e', 64),
        Config = new { Image = "ghcr.io/stef-k/wayfarer@" + config.AppDigest, Env = new[] { "PASSWORD=SECRET-BYTES" },
            Labels = new Dictionary<string, string> { ["com.docker.compose.project"] = config.Project,
                ["com.docker.compose.service"] = "wayfarer", ["com.docker.compose.project.working_dir"] = config.Bundle,
                ["com.docker.compose.project.config_files"] = config.Bundle + "/compose.yaml," + config.Bundle + "/external.yaml",
                ["com.docker.compose.oneoff"] = "False", ["unrelated-password"] = "SECRET-BYTES" } },
        Mounts = new object[]
        {
            new { Type = "volume", Name = ActiveStorage.Volume(config, "app-data"), Source = "/docker/app-data", Destination = "/var/lib/wayfarer", RW = true },
            new { Type = "volume", Name = ActiveStorage.Volume(config, "app-cache"), Source = "/docker/app-cache", Destination = "/var/cache/wayfarer", RW = true },
            new { Type = "volume", Name = ActiveStorage.Volume(config, "app-logs"), Source = "/docker/app-logs", Destination = "/var/log/wayfarer", RW = true },
            new { Type = "bind", Source = root + "/secrets/app-password", Destination = "/run/secrets/app-password", RW = false }
        },
        NetworkSettings = new { Networks = new Dictionary<string, object> { [config.Project + "_backend"] = new { }, [config.Project + "_edge"] = new { } } }
    });

    /// <summary>The receipt has contiguous forward checkpoints and no normal-to-purge or terminal rollback path.</summary>
    [Fact]
    public void ReceiptPhasesAndHashAreValidated()
    {
        var plan = Plan();
        var receipt = new UninstallReceipt { Plan = plan, PlanHash = plan.Hash() };
        receipt.Validate(plan.Root);
        Assert.Throws<UsageException>(() => (receipt with { PlanHash = new string('b', 64) }).Validate(plan.Root));
        Assert.Throws<UsageException>(() => (receipt with { Phase = UninstallPhase.VolumesRemoved }).Validate(plan.Root));
        Assert.Throws<UsageException>(() => (receipt with { Phase = (UninstallPhase)100 }).Validate(plan.Root));
        Assert.Throws<UsageException>(() => receipt.Advance(UninstallPhase.Preserved));
        receipt = receipt.Advance(UninstallPhase.Fenced).Advance(UninstallPhase.RuntimeRemoved).Advance(UninstallPhase.Preserved);
        Assert.True(receipt.Terminal);
        Assert.Throws<UsageException>(() => receipt.Advance(UninstallPhase.Authorized));
        var purge = plan with { Mode = UninstallMode.Purge, Resources = plan.Resources.Select(r =>
            r.Kind == UninstallResourceKind.Volume ? r with { Action = UninstallAction.Remove } : r).ToArray() };
        var purging = new UninstallReceipt { Plan = purge, PlanHash = purge.Hash() };
        purging = purging.Advance(UninstallPhase.Fenced).Advance(UninstallPhase.RuntimeRemoved).Advance(UninstallPhase.VolumesRemoved);
        Assert.Throws<UsageException>(() => purging.Advance(UninstallPhase.Preserved));
        Assert.True(purging.Advance(UninstallPhase.Purged).Terminal);
    }

    /// <summary>Concrete fixture authority carries no credentials and mirrors a release-backed installation before backup opt-in.</summary>
    internal static Deployment Config() => new()
    {
        Schema = 4, Bundle = "/bundle", Hostname = "wayfarer.example.org", Mode = "external",
        AppDigest = "sha256:" + new string('a', 64),
        Release = new ReleaseAuthority("v1.9.22", new string('a', 64), "1.9.22", new string('a', 64))
    };

    /// <summary>Only stable Docker volume identity and ownership facts are simulated.</summary>
    internal static JsonElement Volume(Deployment config, string role, string suffix = "") => JsonSerializer.SerializeToElement(new
    {
        Name = ActiveStorage.Volume(config, role) + suffix, CreatedAt = "2026-10-05T00:00:00Z", Driver = "local", Scope = "local",
        Mountpoint = "/docker/volumes/" + ActiveStorage.Volume(config, role) + suffix + "/_data",
        DataDirectory = new { DeviceMajor = 8, DeviceMinor = 1, Inode = 123, User = 1654, Group = 1654, Mode = 16832,
            CreatedUtc = DateTime.UnixEpoch },
        Options = (object?)null, Labels = new Dictionary<string, string>
        { ["com.docker.compose.project"] = config.Project, ["com.docker.compose.volume"] = role }
    });

    /// <summary>One complete normal plan supports independent grammar, hash and state tests.</summary>
    internal static UninstallPlan Plan()
    {
        var config = Config();
        var history = new UninstallHistory([], []);
        var resources = UninstallInventory.Build("/etc/wayfarer", config, UninstallMode.Normal, history, [], [],
            [Volume(config, "db-data"), Volume(config, "app-data")]);
        return new UninstallPlan(Guid.NewGuid(), "/etc/wayfarer", config, config.Release!,
            UninstallStartingState.Active, UninstallMode.Normal, UninstallBackup.Waived,
            Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(config)),
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(config.EnvironmentFile("/etc/wayfarer"))),
            new string('a', 64), new string('a', 64), history.Fingerprint(), resources);
    }
}
