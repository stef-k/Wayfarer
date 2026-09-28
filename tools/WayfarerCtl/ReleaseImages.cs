using System.Text.Json;

namespace WayfarerCtl;

/// <summary>Independent local image verification; metadata never causes acquisition or network/DB contact.</summary>
public sealed class ReleaseImagesVerifier(IProcessRunner runner)
{
    /// <summary>Missing images are unavailable; any available but contradictory identity is a hard failure.</summary>
    public async Task<bool> VerifyAsync(ReleaseBundle bundle, CancellationToken token)
    {
        var manifest = bundle.Manifest;
        var images = manifest.Images;
        var available = true;
        foreach (var (repository, digest) in new[] { ("ghcr.io/stef-k/wayfarer", images.ApplicationDigest),
            ("ghcr.io/stef-k/wayfarer-db", images.DatabaseDigest), ("caddy", images.CaddyDigest) })
        {
            var result = await runner.RunAsync(["image", "inspect", repository + "@" + digest], null, token);
            if (result.Code != 0) { available = false; continue; }
            using var document = JsonDocument.Parse(result.Output);
            var actual = document.RootElement[0];
            if (actual.GetProperty("Os").GetString() != "linux" || actual.GetProperty("Architecture").GetString() != "amd64" ||
                !actual.GetProperty("RepoDigests").EnumerateArray().Any(value => value.GetString() == repository + "@" + digest))
                throw new IOException("Local image digest/platform mismatch.");
            if (repository != "ghcr.io/stef-k/wayfarer") continue;
            var labels = actual.GetProperty("Config").GetProperty("Labels");
            if (labels.GetProperty("org.opencontainers.image.source").GetString() != manifest.Repository ||
                labels.GetProperty("org.opencontainers.image.revision").GetString() != manifest.SourceRevision ||
                labels.GetProperty("org.opencontainers.image.version").GetString() != images.OciVersion ||
                images.PlatformDigest != images.ApplicationDigest)
                throw new IOException("Local application OCI identity mismatch; OCI indexes are not supported by v1.");
        }
        if (!available) return false;
        var containers = new RestoreContainers(runner);
        var prefix = "wayfarer-release-restore-" + Guid.NewGuid().ToString("N");
        var version = await containers.RunAsync(prefix + "-version",
            [.. RestoreContainers.Unprivileged(), "--network=none", "--entrypoint=dotnet",
                "ghcr.io/stef-k/wayfarer@" + images.ApplicationDigest, "Wayfarer.dll", "version"], token);
        if (version.Trim() != "Wayfarer " + manifest.Application.CompiledVersion) throw new IOException("Compiled version mismatch.");
        var worker = await containers.RunAsync(prefix + "-worker",
            [.. RestoreContainers.Unprivileged(), "--network=none", "--volume", Path.Combine(bundle.Directory, "wayfarer-recovery") + ":/worker:ro",
                "--entrypoint=/worker", "ghcr.io/stef-k/wayfarer-db@" + images.DatabaseDigest, "runtime-check"], token);
        using var runtime = JsonDocument.Parse(worker);
        if (runtime.RootElement.GetProperty("Schema").GetInt32() != 1) throw new IOException("Recovery runtime contract mismatch.");
        var contract = await containers.RunAsync(prefix + "-contract",
            [.. RestoreContainers.Unprivileged(), "--network=none", "--volume",
                Path.Combine(bundle.Directory, "WayfarerRecoverySource.dll") + ":/inspection.dll:ro",
                "--entrypoint=dotnet", "ghcr.io/stef-k/wayfarer@" + images.ApplicationDigest,
                "exec", "--runtimeconfig", "/app/Wayfarer.runtimeconfig.json", "--depsfile", "/app/Wayfarer.deps.json",
                "/inspection.dll", "release-contract"], token);
        using var actualContract = JsonDocument.Parse(contract);
        var expected = JsonSerializer.SerializeToElement(manifest.Application);
        foreach (var property in actualContract.RootElement.EnumerateObject())
            if (!expected.TryGetProperty(property.Name, out var value) || !JsonElement.DeepEquals(property.Value, value))
                throw new IOException("Application-owned release compatibility differs from manifest.");
        if (actualContract.RootElement.EnumerateObject().Count() != expected.EnumerateObject().Count() - 1)
            throw new IOException("Incomplete application release contract.");
        foreach (var name in new[] { "version", "worker", "contract" })
            await containers.Required(["rm", prefix + "-" + name], token);
        return true;
    }
}
