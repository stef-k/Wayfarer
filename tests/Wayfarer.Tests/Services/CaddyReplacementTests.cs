using System.Text.Json;
using WayfarerCtl;
using WayfarerRecovery;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Focused replacement admission and retained-owner tests leave the existing migration matrix to its owner.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class CaddyReplacementTests
{
    /// <summary>The same shipped policy owns both native source fingerprints and the only allowed template delta.</summary>
    private static (ReleaseBundle Source, ReleaseBundle Target) Pair(string platform)
    {
        using var stream = typeof(CaddyReplacement).Assembly.GetManifestResourceStream("WayfarerCtl.caddy-replacement.json")!;
        using var document = JsonDocument.Parse(stream);
        var policy = document.RootElement;
        var files = policy.GetProperty("unchanged").EnumerateObject().Select(file =>
            new ReleaseFile(file.Name, file.Value.GetString()!, "file", ReleaseContract.Mode(file.Name)))
            .Append(new("compose.yaml", policy.GetProperty("sourceCompose").GetString()!, "file", 420))
            .Append(new("wayfarerctl", new string('a', 64), "file", 365)).ToArray();
        var source = new ReleaseManifest(1, 1, 1, "stable", "1.9.22", "v1.9.22", "https://github.com/stef-k/Wayfarer",
            policy.GetProperty("sourceRevision").GetString()!, platform,
            new("ghcr.io/stef-k/wayfarer", "sha256:" + new string('a', 64), "sha256:" + new string('a', 64), "1.9.22",
                "sha256:" + new string('b', 64), ReleaseContract.LegacyCaddyDigest, 18, "3.6.4", "1.8", "UTF8", "C.UTF-8", "C.UTF-8", "c"),
            new("1.9.22", ["20260101000000_Initial"], "20260101000000_Initial", "quartz", [2], new string('c', 64),
                "Wayfarer", "uploads", "data-protection", "ready", "1.9.22.0"), ReleaseContract.CurrentOperator, [], files, null);
        var fingerprint = policy.GetProperty("sources").GetProperty(platform).GetString()!;
        var target = source with { Version = "9999.0.0", Tag = "v9999.0.0",
            Images = source.Images with { PlatformDigest = "sha256:" + new string('d', 64), CaddyDigest = ReleaseContract.CaddyDigest },
            Sources = [new("1.9.22", fingerprint, source.Application.TerminalMigration, true, false, "manual-recovery", "held recovery")],
            Files = files.Select(file => file.Path == "compose.yaml" ? file with { Sha256 = policy.GetProperty("targetCompose").GetString()! } : file).ToArray() };
        return (new("/source", source, fingerprint), new("/target", target, new string('e', 64)));
    }

    /// <summary>Ordering and exact source permission remain necessary even with explicit replacement admission.</summary>
    [Fact]
    public void OnlyExplicitForwardQualifiedPairPasses()
    {
        var (source, target) = Pair(NativePlatform.Current);
        Assert.Throws<UsageException>(() => UpdateOptions.Boundary(source, target));
        Assert.Equal(source.Fingerprint, UpdateOptions.Boundary(source, target, replacement: true).Fingerprint);
        Assert.Throws<UsageException>(() => UpdateOptions.Boundary(target, source, replacement: true));
        Assert.Throws<UsageException>(() => UpdateOptions.Boundary(source with { Fingerprint = new string('f', 64) }, target, replacement: true));
        Assert.Throws<UsageException>(() => UpdateOptions.Boundary(source, target with { Manifest = target.Manifest with { Sources = [] } }, replacement: true));
        Assert.Throws<UsageException>(() => UpdateOptions.Boundary(source, target with { Manifest = target.Manifest with
            { Images = target.Manifest.Images with { DatabaseDigest = "sha256:" + new string('f', 64) } } }, replacement: true));
    }

    /// <summary>Both native authorities are supported; no unrelated template change gains admission.</summary>
    [Theory]
    [InlineData("linux/amd64")]
    [InlineData("linux/arm64")]
    public void ExactTemplatesAndNativeSourceAreRequired(string platform)
    {
        var (source, target) = Pair(platform);
        CaddyReplacement.Require(source, target);
        foreach (var file in target.Manifest.Files.Where(file => file.Path != "wayfarerctl"))
            Assert.Throws<UsageException>(() => CaddyReplacement.Require(source, target with { Manifest = target.Manifest with
            { Files = target.Manifest.Files.Select(value => value.Path == file.Path ? value with { Sha256 = new string('f', 64) } : value).ToArray() } }));
    }

    /// <summary>Schema one keeps the historical installed owner; schema two cannot substitute that owner for the target.</summary>
    [Fact]
    public void ReplacementOwnerCannotBeSubstituted()
    {
        var (source, target) = Pair(NativePlatform.Current);
        var current = new Deployment { Release = ReleaseAuthority.From(source) };
        var next = current with { Release = ReleaseAuthority.From(target) };
        var plan = new UpdatePlan(Guid.NewGuid(), "/root", current, next, next.Release!, target.Manifest.Sources[0], [], "", "", "", new(1, 2, 2, 2, 1, 1))
            { UpdateProtocol = 2 };
        CaddyReplacement.RequireOwner(plan);
        Assert.Throws<IOException>(() => CaddyReplacement.RequireOwner(plan with { OperatorOwner = current.Release! }));
        Assert.Throws<IOException>(() => CaddyReplacement.RequireOwner(plan with { UpdateProtocol = 1 }));
        CaddyReplacement.RequireOwner(plan with { UpdateProtocol = 1, OperatorOwner = current.Release! });
    }
}
