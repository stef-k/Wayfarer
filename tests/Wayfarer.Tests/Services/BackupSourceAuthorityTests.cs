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
    public async Task ConfigurationPersistsRetainedCaptureAuthority(bool stable, string status)
    {
        var bundle = releases.RetainOperator(Root, "1.9.21", runningOperator: false, stable);
        var config = Install(bundle);
        var runner = new BackupRunner(config, bundle);
        using var operation = Setup.Lock(Root);
        var next = await new BackupConfiguration(runner).ConfigureAsync(Root, config,
            ["--destination", destination.Path, "--payload", Path.Combine(bundle.Directory, "wayfarer-recovery")], default);
        var selected = Deployment.Load(Root);
        Assert.Equal(status, selected.Backup!.Source.ReleaseStatus);
        Assert.Equal(bundle.Manifest.Application.WorkerVersion, selected.Backup.Source.WorkerVersion);
        Assert.Equal(next.Backup!.Generation, selected.Backup.Generation);
        bundle.Corroborate(selected.Backup.Source);
        BackupCompose.Check(Root, selected);
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
    private sealed class BackupRunner(Deployment config, ReleaseBundle bundle) : IProcessRunner
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
                _ when args is ["compose", "version", "--short"] => "2.24.4",
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
