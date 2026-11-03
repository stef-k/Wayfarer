using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Point = NetTopologySuite.Geometries.Point;
using Wayfarer.CommandLine;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using WayfarerCtl;
using WayfarerRecovery;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>One joined proof combines real schema/history checkpoints with the managed helper/receipt process boundary.</summary>
[Collection(PostgresImportTestCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[Trait("Category", "RequiresRoot")]
public sealed class ManagedHistoryUpgradePostgresTests(PostgresMigrationTestFixture fixture) : IClassFixture<PostgresMigrationTestFixture>
{
    /// <summary>Schema success cannot accept partial preparation; lost helper replies resume without schema replay or batch reset.</summary>
    [PostgresFact]
    public async Task SchemaThenInterruptedPreparationAndLostReplyRequireIndependentCompletion()
    {
        ProtectedFiles.RequireRoot();
        using var directory = new TestDirectory(); using var destination = new TestDirectory();
        File.SetUnixFileMode(directory.Path, ProtectedFiles.PrivateDirectory);
        using var releases = new ReleaseBundleTests();
        var bundle = releases.RetainOperator(directory.Path, "1.9.21", runningOperator: false);
        var receipt = Receipt(directory.Path, destination.Path, bundle);
        ProtectedFiles.Create(Path.Combine(directory.Path, "installation.json"), receipt.Plan.OldConfiguration);
        BackupConfiguration.ProvisionControl(directory.Path, bootstrap: true);
        receipt.Save(directory.Path);
        var owner = await fixture.CreateUserAsync();
        await using (var seed = fixture.CreateContext())
        {
            await QuartzSchemaInstaller.EnsureQuartzTablesExistAsync(seed.Database.GetDbConnection(), default);
            seed.Add(new Location { UserId = owner.Id, Notes = "<p>canonical café</p>", Source = "import",
                IdempotencyKey = Guid.NewGuid(), Coordinates = new Point(139.76, 35.68) { SRID = 4326 },
                Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow, TimeZoneId = "UTC" });
            await seed.SaveChangesAsync();
            await seed.Locations.ExecuteUpdateAsync(set => set.SetProperty(row => row.HistoryTimeVersion, (string?)null)
                .SetProperty(row => row.HistoryTimeStatus, (string?)null).SetProperty(row => row.HistoryTimeFailureCode, (string?)null)
                .SetProperty(row => row.CaptureLocalTimestamp, (DateTime?)null).SetProperty(row => row.CaptureTimeZoneId, (string?)null)
                .SetProperty(row => row.CaptureOffsetSeconds, (int?)null).SetProperty(row => row.HistorySearchText, (string?)null)
                .SetProperty(row => row.HistoryTextVersion, (string?)null));
            await seed.HistoryPreparationStates.ExecuteDeleteAsync();
        }
        var runner = new UpgradeProcess(fixture, receipt, bundle);
        using var host = Setup.Lock(directory.Path);
        using var recovery = new RecoveryLock(Path.Combine(directory.Path, "recovery-control/recovery.lock"));
        var runtime = new UpdateRuntime(runner);
        await Assert.ThrowsAsync<IOException>(() => runtime.MigrateAsync(directory.Path, receipt, default));
        var interrupted = UpdateReceipt.Load(directory.Path)!;
        Assert.Equal(UpdatePhase.MigrationStarted, interrupted.Phase); Assert.False(interrupted.WritesPossible);
        await using var verify = fixture.CreateContext();
        var checkpoint = await verify.HistoryPreparationStates.AsNoTracking().SingleAsync(state => state.Kind == "time");
        Assert.True(checkpoint.Prepared > 0); Assert.False(checkpoint.Ready);
        runner.LoseNextStartReply = true;
        await Assert.ThrowsAsync<IOException>(() => runtime.MigrateAsync(directory.Path, interrupted, default));
        var unacknowledged = UpdateReceipt.Load(directory.Path)!;
        Assert.Equal(UpdatePhase.MigrationStarted, unacknowledged.Phase);
        Assert.All(await new LocationHistoryPreparation(verify).InspectAsync(default), status => Assert.True(status.Ready));
        var completed = await runtime.MigrateAsync(directory.Path, unacknowledged, default);
        Assert.Equal(UpdatePhase.MigrationConfirmed, completed.Phase);
        Assert.True(UpdateHistoryPreparation.Ready(completed.HistoryReconciliation!));
        Assert.Equal(2, runner.HistoryStarts); Assert.Equal(2, completed.HistoryAttempts);
        Assert.DoesNotContain(runner.Creates, args => args[^1] == "migrate");
        Assert.Equal(checkpoint.Prepared, (await verify.HistoryPreparationStates.AsNoTracking().SingleAsync(state => state.Kind == "time")).Prepared);
        Assert.All(await verify.Locations.AsNoTracking().ToListAsync(), row => Assert.Equal("<p>canonical café</p>", row.Notes));
        // A restored Ready flag with stale rows cannot authorize even the first activation pointer write.
        await verify.Locations.ExecuteUpdateAsync(set => set.SetProperty(row => row.HistoryTextVersion, "restored-old"));
        var restored = await verify.Database.SqlQueryRaw<string>("SELECT row_to_json(l)::text AS \"Value\" FROM \"Locations\" l").ToArrayAsync();
        await Assert.ThrowsAsync<IOException>(() => new UpdateActivation(runner).ActivateAsync(directory.Path, completed, default));
        Assert.Equal(receipt.Plan.OldConfiguration, File.ReadAllText(Path.Combine(directory.Path, "installation.json")));
        // Restore ownership uses the same read-only reconciliation and never starts preparation.
        await new UpdateHistoryPreparation(runner).ReconcileAsync(directory.Path, completed, default);
        Assert.Equal(2, runner.HistoryStarts);
        Assert.Equal(restored, await verify.Database.SqlQueryRaw<string>("SELECT row_to_json(l)::text AS \"Value\" FROM \"Locations\" l").ToArrayAsync());
    }

    /// <summary>Use the existing retained-bundle/recovery authority fixtures; no live installation or Docker service is selected.</summary>
    private static UpdateReceipt Receipt(string root, string destination, ReleaseBundle bundle)
    {
        var config = new Deployment { Schema = 4, Installation = Guid.NewGuid(), Release = ReleaseAuthority.From(bundle),
            Bundle = bundle.Directory, Hostname = "wayfarer.example.org", Mode = "external", Platform = NativePlatform.Current,
            AppDigest = bundle.Manifest.Images.PlatformDigest, DbDigest = bundle.Manifest.Images.DatabaseDigest,
            Backup = new BackupPolicy { Destination = destination, Payload = Path.Combine(bundle.Directory, "wayfarer-recovery"),
                PayloadSha256 = new string('a', 64), Generation = new string('b', 64), Uploads = "uploads", Ring = "data-protection",
                Source = bundle.Target("wayfarer", true) } };
        var plan = new UpdatePlan(Guid.NewGuid(), root, config, config, config.Release,
            new ReleaseSourceBoundary("1.9.21", bundle.Fingerprint, bundle.Manifest.Application.TerminalMigration,
                true, false, "forward-inspection-or-recovery", ""), [], JsonSerializer.Serialize(config), config.EnvironmentFile(root),
            new string('c', 64), new UpdateCapacity(1, 2, 2, 2, 0, 0)) { TargetMigrations = bundle.Manifest.Application.Migrations };
        return new UpdateReceipt { Plan = plan, PlanHash = plan.Hash(), Phase = UpdatePhase.MigrationStarted,
            MigrationContainer = config.Project + "-update-migrate-" + plan.Operation.ToString("N"),
            RecoveryArchive = Guid.NewGuid(), RecoveryName = "held.tar", RecoverySha256 = new string('a', 64) };
    }

    /// <summary>Docker transport is recorded; target schema and preparation execute the real application against the guarded database.</summary>
    private sealed class UpgradeProcess(PostgresMigrationTestFixture fixture, UpdateReceipt receipt, ReleaseBundle bundle) : IProcessRunner
    {
        private readonly Dictionary<string, string[]> probes = new();
        internal List<string[]> Creates { get; } = [];
        internal int HistoryStarts { get; private set; }
        internal bool LoseNextStartReply { get; set; }

        public async Task<ProcessResult> RunAsync(string[] args, string? input, CancellationToken cancellation, Action<string>? lineOutput = null)
        {
            if (args[0] == "create")
            {
                Creates.Add(args); var name = args[Array.IndexOf(args, "--name") + 1]; probes[name] = args;
                return new(0, name);
            }
            if (args[0] == "start" && args[1].Contains("-update-history-"))
            {
                HistoryStarts++;
                await using var db = fixture.CreateContext();
                using var services = new ServiceCollection().BuildServiceProvider();
                using var stop = new CancellationTokenSource();
                var output = new InterruptProgress(HistoryStarts == 1 ? stop : null);
                try { await LifecycleCli.DatabaseAsync("prepare-history", db, services, output, stop.Token); }
                catch (OperationCanceledException) { }
                if (LoseNextStartReply) { LoseNextStartReply = false; return new(1, "Lost start acknowledgement"); }
                return new(0, "");
            }
            if (args[0] == "logs" && !args.Contains("--follow")) return new(0, await ProbeOutputAsync(probes[args[^1]]));
            if (args[0] == "inspect")
            {
                if (args.Contains("--format")) return new(0, "false");
                var history = args[1] != receipt.MigrationContainer;
                return new(0, JsonSerializer.Serialize(new[] { Helper(receipt, history, exitCode: history && HistoryStarts > 1 ? 0 : 130) }));
            }
            if (args[0] == "network") return new(0, "[{\"Internal\":true,\"Containers\":{}}]");
            if (args[0] == "info") return new(0, "/tmp");
            if (args.Contains("psql")) return new(0, "1024");
            if (args[0] is "start" or "wait" or "stop" or "rm" or "ps" or "logs") return new(0, args[0] == "wait" ? "0" : "");
            throw new InvalidOperationException("Unexpected test process command: " + args[0]);
        }

        /// <summary>Run independent real target schema/history reads instead of assuming helper completion.</summary>
        private async Task<string> ProbeOutputAsync(string[] args)
        {
            await using var db = fixture.CreateContext();
            if (args[^1] == "history-status")
            {
                using var services = new ServiceCollection().BuildServiceProvider(); using var output = new StringWriter();
                await LifecycleCli.DatabaseAsync("history-status", db, services, output, default);
                return output.ToString();
            }
            if (args[^1] == "/inspection.dll")
            {
                await ApplicationReadiness.ValidateSchemaAsync(db, default);
                var app = bundle.Manifest.Application;
                return JsonSerializer.Serialize(new { Schema = 2, ApplicationVersion = app.CompiledVersion,
                    ExpectedMigrations = app.Migrations, app.QuartzCompatibilityContract, ApplicationName = app.DataProtectionName,
                    app.Uploads, app.Ring, ProtectedCredentials = "none present", QuartzSnapshotFingerprint = new string('d', 64) });
            }
            return "1073741824 1"; // Existing capacity probe output, in KiB; no resource sizing claim.
        }
    }

    /// <summary>Exact stopped target maintenance facts include immutable mounts and separate schema/preparation commands.</summary>
    internal static JsonElement Helper(UpdateReceipt receipt, bool history, bool running = false, string? image = null, int exitCode = 0)
    {
        var name = history ? receipt.Plan.Current.Project + "-update-history-" + receipt.Plan.Operation.ToString("N") : receipt.MigrationContainer!;
        return JsonSerializer.SerializeToElement(new { Id = name, Name = "/" + name,
            Config = new { Image = image ?? "ghcr.io/stef-k/wayfarer@" + receipt.Plan.Target.AppDigest, User = "1654:1654",
                Entrypoint = new[] { "dotnet" }, Env = new[] {
                    "ConnectionStrings__DefaultConnection=Host=db;Database=wayfarer;Username=wayfarer;Timeout=5",
                    "Database__PasswordFile=/run/secrets/app-password" },
                Cmd = new[] { "Wayfarer.dll", "database", history ? "prepare-history" : "migrate" },
                Labels = new Dictionary<string, string> { ["wayfarer.update"] = receipt.Plan.Operation.ToString("D"),
                    ["wayfarer.update-project"] = receipt.Plan.Current.Project,
                    ["wayfarer.history-update"] = receipt.Plan.Operation.ToString("D") } },
            HostConfig = new { ReadonlyRootfs = true, Privileged = false, RestartPolicy = new { Name = "no" },
                NetworkMode = receipt.Plan.Current.Project + "_backend" }, State = new { Running = running, ExitCode = exitCode },
            Mounts = new[] {
                new { Type = "volume", Name = ActiveStorage.Volume(receipt.Plan.Current, "app-data"), Destination = "/var/lib/wayfarer", Source = "", RW = false },
                new { Type = "bind", Name = "", Destination = "/run/secrets/app-password", Source = Path.Combine(receipt.Plan.Root, "secrets/app-password"), RW = false } } });
    }

    /// <summary>Cancellation at a real progress notification proves the batch committed before the helper was interrupted.</summary>
    private sealed class InterruptProgress(CancellationTokenSource? stop) : StringWriter
    {
        public override Task WriteLineAsync(string? value)
        {
            if (value?.StartsWith("History preparation: time; prepared=") == true && !value.Contains("prepared=0;")) stop?.Cancel();
            return base.WriteLineAsync(value);
        }
    }
}
