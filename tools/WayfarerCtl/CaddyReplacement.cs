using System.Text.Json;

namespace WayfarerCtl;

/// <summary>The one qualified proxy bridge; the embedded policy is shared with publication admission.</summary>
public static class CaddyReplacement
{
    private static readonly JsonElement Policy = ReadPolicy();

    private static JsonElement ReadPolicy()
    {
        using var stream = typeof(CaddyReplacement).Assembly.GetManifestResourceStream("WayfarerCtl.caddy-replacement.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    /// <summary>Exact public predecessor and template hashes exclude version skips, reverse updates and topology drift.</summary>
    public static void Require(ReleaseBundle source, ReleaseBundle target)
    {
        var before = source.Manifest;
        var after = target.Manifest;
        string FileHash(ReleaseManifest manifest, string path) => manifest.Files.Single(file => file.Path == path).Sha256;
        if (before.Status != "stable" || before.Version != Policy.GetProperty("sourceVersion").GetString() ||
            before.SourceRevision != Policy.GetProperty("sourceRevision").GetString() ||
            !Policy.GetProperty("sources").TryGetProperty(before.Platform, out var fingerprint) || fingerprint.GetString() != source.Fingerprint ||
            before.Images.CaddyDigest != Policy.GetProperty("sourceCaddy").GetString() ||
            after.Images.CaddyDigest != Policy.GetProperty("targetCaddy").GetString() ||
            FileHash(before, "compose.yaml") != Policy.GetProperty("sourceCompose").GetString() ||
            FileHash(after, "compose.yaml") != Policy.GetProperty("targetCompose").GetString())
            throw new UsageException("Unsupported Caddy replacement release/template authority.");
        foreach (var file in Policy.GetProperty("unchanged").EnumerateObject())
            if (FileHash(before, file.Name) != file.Value.GetString() || FileHash(after, file.Name) != file.Value.GetString())
                throw new UsageException("Caddy replacement changes unrelated template authority.");
        if (!after.Operator.UpdateReceiptSchemas.Contains(2) || !after.Operator.RestoreReceiptSchemas.Contains(2))
            throw new UsageException("Target operator lacks replacement recovery protocols.");
    }

    /// <summary>Protocol one keeps its installed owner; protocol two belongs exclusively to the bound target operator.</summary>
    public static void RequireOwner(UpdatePlan plan)
    {
        var expected = plan.UpdateProtocol == 2 ? plan.Target.Release : plan.Current.Release;
        if (plan.UpdateProtocol is not (1 or 2) || plan.OperatorOwner != expected)
            throw new IOException("Update protocol/operator owner mismatch.");
    }
    /// <summary>Proxy identity includes the protected configuration, named TLS stores and the exact service network.</summary>
    public static void VerifyProxy(string root, Deployment config, JsonElement container)
    {
        if (config.Mode != "managed") throw new IOException("External ingress cannot own a managed proxy.");
        Preflight.VerifyRetainedResource(config, "container", container, root);
        var actual = container.GetProperty("Config");
        if (actual.GetProperty("Labels").GetProperty("com.docker.compose.service").GetString() != "caddy" ||
            actual.GetProperty("Image").GetString() != "caddy@" + config.CaddyDigest ||
            !actual.GetProperty("Cmd").Deserialize<string[]>()!.SequenceEqual(new[] { "caddy", "run", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile" }) ||
            !actual.GetProperty("Env").EnumerateArray().Any(value => value.GetString() == "PUBLIC_HOST=" + config.Hostname))
            throw new IOException("Proxy service/image/configuration differs from release.");
        var mounts = container.GetProperty("Mounts").EnumerateArray().ToArray();
        if (mounts.Length != 3) throw new IOException("Unexpected proxy mounts.");
        foreach (var (role, destination) in new[] { ("caddy-data", "/data"), ("caddy-config", "/config") })
            if (!mounts.Any(mount => mount.GetProperty("Type").GetString() == "volume" &&
                mount.GetProperty("Name").GetString() == ActiveStorage.Volume(config, role) &&
                mount.GetProperty("Destination").GetString() == destination && mount.GetProperty("RW").GetBoolean()))
                throw new IOException("Proxy TLS volume changed.");
        if (!mounts.Any(mount => mount.GetProperty("Type").GetString() == "bind" &&
            mount.GetProperty("Source").GetString() == Path.Combine(config.Bundle, "caddy/Caddyfile") &&
            mount.GetProperty("Destination").GetString() == "/etc/caddy/Caddyfile" && !mount.GetProperty("RW").GetBoolean()))
            throw new IOException("Proxy configuration mount changed.");
        var networks = container.GetProperty("NetworkSettings").GetProperty("Networks").EnumerateObject().Select(value => value.Name);
        if (!networks.SequenceEqual(new[] { config.Project + "_edge" })) throw new IOException("Proxy network changed.");
    }

    /// <summary>Corroborate the actual running payload against Docker's content-addressed native image selection.</summary>
    public static async Task VerifyImageAsync(IProcessRunner runner, Deployment config, JsonElement container, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await new RestoreContainers(runner).Required(["image", "inspect", "caddy@" + config.CaddyDigest], token));
        var image = document.RootElement[0];
        if (container.GetProperty("Image").GetString() != image.GetProperty("Id").GetString() ||
            image.GetProperty("Os").GetString() != "linux" || image.GetProperty("Architecture").GetString() != config.RuntimePlatform.Split('/')[1] ||
            !image.GetProperty("RepoDigests").EnumerateArray().Any(value => value.GetString() == "caddy@" + config.CaddyDigest))
            throw new IOException("Actual proxy payload differs from retained image.");
    }
}
