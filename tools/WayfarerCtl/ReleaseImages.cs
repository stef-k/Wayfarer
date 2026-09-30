using System.Text.Json;

namespace WayfarerCtl;

/// <summary>Independent local image verification; metadata never causes acquisition or network/DB contact.</summary>
public sealed class ReleaseImagesVerifier(IProcessRunner runner)
{
    /// <summary>Missing images are unavailable; any available but contradictory identity is a hard failure.</summary>
    public async Task<bool> VerifyAsync(ReleaseBundle bundle, CancellationToken token)
    {
        ProtectedFiles.SafePath(bundle.Directory);
        bundle = ReleaseBundle.Validate(bundle.Directory, installed: true);
        var manifest = bundle.Manifest;
        ReleaseContract.RequireUse(manifest, ReleaseCommands.OperatorVersion);
        var images = manifest.Images;
        var available = true;
        // Publication binds the release index; offline probes execute only the exact selected manifest.
        foreach (var (repository, digest) in new[] { ("ghcr.io/stef-k/wayfarer", images.PlatformDigest),
            ("ghcr.io/stef-k/wayfarer-db", images.DatabaseDigest), ("caddy", images.CaddyDigest) })
        {
            var result = await runner.RunAsync(["image", "inspect", repository + "@" + digest], null, token);
            if (result.Code != 0) { available = false; continue; }
            using var document = JsonDocument.Parse(result.Output);
            var actual = document.RootElement[0];
            if (actual.GetProperty("Os").GetString() != "linux" || actual.GetProperty("Architecture").GetString() != manifest.Platform.Split('/')[1] ||
                !actual.GetProperty("RepoDigests").EnumerateArray().Any(value => value.GetString() == repository + "@" + digest))
                throw new IOException("Local image digest/platform mismatch.");
            if (repository != "ghcr.io/stef-k/wayfarer") continue;
            var labels = actual.GetProperty("Config").GetProperty("Labels");
            if (labels.GetProperty("org.opencontainers.image.source").GetString() != manifest.Repository ||
                labels.GetProperty("org.opencontainers.image.revision").GetString() != manifest.SourceRevision ||
                labels.GetProperty("org.opencontainers.image.version").GetString() != images.OciVersion)
                throw new IOException("Local application OCI identity mismatch.");
        }
        if (!available) return false;
        var containers = new RestoreContainers(runner);
        var prefix = "wayfarer-release-restore-" + Guid.NewGuid().ToString("N");
        await VerifyDatabaseAsync(containers, prefix, images.DatabaseDigest, token);
        var version = await ProbeAsync(containers, prefix + "-version",
            [.. RestoreContainers.Unprivileged(), "--network=none", "--entrypoint=dotnet",
                "ghcr.io/stef-k/wayfarer@" + images.PlatformDigest, "Wayfarer.dll", "version"], token);
        if (version.Trim() != "Wayfarer " + manifest.Application.CompiledVersion) throw new IOException("Compiled version mismatch.");
        var worker = await ProbeAsync(containers, prefix + "-worker",
            [.. RestoreContainers.Unprivileged(), "--network=none", "--volume", Path.Combine(bundle.Directory, "wayfarer-recovery") + ":/worker:ro",
                "--entrypoint=/worker", "ghcr.io/stef-k/wayfarer-db@" + images.DatabaseDigest, "runtime-check"], token);
        using var runtime = JsonDocument.Parse(worker);
        if (runtime.RootElement.GetProperty("Schema").GetInt32() != 1 ||
            runtime.RootElement.GetProperty("Version").GetString() != manifest.Application.WorkerVersion) throw new IOException("Recovery runtime contract mismatch.");
        var protocol = await ProbeAsync(containers, prefix + "-operator",
            [.. RestoreContainers.Unprivileged(), "--network=none", "--volume", Path.Combine(bundle.Directory, "wayfarerctl") + ":/operator:ro",
                "--entrypoint=/operator", "ghcr.io/stef-k/wayfarer-db@" + images.DatabaseDigest, "release", "protocol"], token);
        using var actualOperator = JsonDocument.Parse(protocol);
        if (!JsonElement.DeepEquals(actualOperator.RootElement, JsonSerializer.SerializeToElement(manifest.Operator)))
            throw new IOException("Bundled operator protocol/version mismatch.");
        var contract = await ProbeAsync(containers, prefix + "-contract",
            [.. RestoreContainers.Unprivileged(), "--network=none", "--volume",
                Path.Combine(bundle.Directory, "WayfarerRecoverySource.dll") + ":/inspection.dll:ro",
                "--entrypoint=dotnet", "ghcr.io/stef-k/wayfarer@" + images.PlatformDigest,
                "exec", "--runtimeconfig", "/app/Wayfarer.runtimeconfig.json", "--depsfile", "/app/Wayfarer.deps.json",
                "/inspection.dll", "release-contract"], token);
        using var actualContract = JsonDocument.Parse(contract);
        var expected = JsonSerializer.SerializeToElement(manifest.Application);
        foreach (var property in actualContract.RootElement.EnumerateObject())
            if (!expected.TryGetProperty(property.Name, out var value) || !JsonElement.DeepEquals(property.Value, value))
                throw new IOException("Application-owned release compatibility differs from manifest.");
        if (actualContract.RootElement.EnumerateObject().Count() != expected.EnumerateObject().Count() - 1)
            throw new IOException("Incomplete application release contract.");
        if (manifest.LegacyCapture is { } capture)
        {
            var legacy = await ProbeAsync(containers, prefix + "-capture",
                [.. RestoreContainers.Unprivileged(), "--network=none", "--volume",
                    Path.Combine(bundle.Directory, "capture/wayfarer-recovery") + ":/worker:ro",
                    "--entrypoint=/worker", "ghcr.io/stef-k/wayfarer-db@" + images.DatabaseDigest, "runtime-check"], token);
            using var legacyRuntime = JsonDocument.Parse(legacy);
            if (legacyRuntime.RootElement.GetProperty("Schema").GetInt32() != 1 ||
                legacyRuntime.RootElement.TryGetProperty("Version", out var observed) && observed.GetString() != capture.WorkerVersion)
                throw new IOException("Historical capture runtime differs from its retained contract.");
        }
        return true;
    }

    /// <summary>A release-specific DB manifest must preserve the exact accepted executable/package contract.</summary>
    private async Task VerifyDatabaseAsync(RestoreContainers containers, string prefix, string digest, CancellationToken token)
    {
        foreach (var (package, expected) in new[] { ("postgresql-17", "17.11-1.pgdg12+2"),
            ("postgresql-17-postgis-3", "3.6.4+dfsg-2.pgdg12+1"), ("postgresql-17-postgis-3-scripts", "3.6.4+dfsg-2.pgdg12+1") })
        {
            var installed = await ProbeAsync(containers, prefix + "-" + package,
                [.. RestoreContainers.Unprivileged(), "--network=none", "--entrypoint=dpkg-query",
                    "ghcr.io/stef-k/wayfarer-db@" + digest, "-W", "-f=${Version}", package], token);
            if (installed.Trim() != expected) throw new IOException("DB package differs from the accepted release contract.");
        }
    }

    /// <summary>Stateless probes retain no installation data; reap only our confirmed-stopped named helper.</summary>
    internal async Task<string> ProbeAsync(RestoreContainers containers, string name, string[] command, CancellationToken token)
    {
        Exception? primary = null;
        try { return await containers.RunAsync(name, command, token); }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                var state = await runner.RunAsync(["inspect", "--format", "{{.State.Running}}", name], null, cleanup.Token);
                if (state.Code == 0 && state.Output.Trim() == "false")
                    await containers.Required(["rm", name], cleanup.Token);
            }
            catch when (primary is not null) { Console.Error.WriteLine("Release probe cleanup failed; original failure retained."); }
        }
    }
}
