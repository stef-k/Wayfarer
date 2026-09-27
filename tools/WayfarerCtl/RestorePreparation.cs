using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Freeze bounded archive bytes and independently trusted target evidence before destructive authorization.</summary>
public sealed class RestorePreparation(IProcessRunner runner)
{
    [DllImport("libc", SetLastError = true)]
    private static extern int chown(string path, uint user, uint group);

    public static string DirectoryFor(string root, Guid operation) => Path.Combine(root, "restore-plans", operation.ToString("N"));

    /// <summary>Prepare only non-authoritative private disk staging; archive parsing runs in an unprivileged helper.</summary>
    public async Task<RestorePlan> PrepareAsync(string root, Deployment config, RestoreOptions options, CancellationToken token)
    {
        var operation = Guid.NewGuid();
        var directory = DirectoryFor(root, operation);
        Directory.CreateDirectory(directory, ProtectedFiles.PrivateDirectory);
        using (var storage = new SafeDirectory(directory)) storage.RequireLocalControl();
        var source = options.Has("--archive") ? Guid.Parse(options.Get("--source-installation")) : config.Installation;
        var name = options.Has("--archive") ? Path.GetFileName(options.Get("--archive")) : options.Basename;
        var destination = options.Has("--archive") ? Path.GetDirectoryName(options.Get("--archive"))! :
            (config.Backup ?? throw new UsageException("Owned restore requires configured backup destination.")).Destination;
        using var input = new SafeDirectory(destination);
        if (name is null)
        {
            var policy = config.Backup!;
            BackupCompose.Check(root, config);
            var restorePayload = options.Get("--restore-payload");
            new BackupPolicy { Payload = restorePayload, PayloadSha256 = BackupPolicy.Fingerprint(restorePayload) }.CheckPayload();
            var mounts = policy.Kind == "local"
                ? new[] { "--volume", destination + ":/destination/slot:ro" }
                : new[] { "--mount", "type=bind,source=" + Path.GetDirectoryName(destination) + ",target=/destination,readonly,bind-propagation=rslave" };
            name = (await new RestoreContainers(runner).RunAsync(config.Project + "-restore-select-" + operation.ToString("N"),
                [.. RestoreContainers.Unprivileged(), "--network=none", "--volume", restorePayload + ":/worker:ro",
                    "--volume", Path.Combine(BackupCompose.DirectoryPath(root, policy), "worker.json") + ":/config/worker.json:ro",
                    .. mounts, "--entrypoint=/worker", "ghcr.io/stef-k/wayfarer-db@" + config.DbDigest, "restore-select"], token)).Trim();
        }
        RestoreOptions.ValidateBasename(name);
        var frozen = Path.Combine(directory, "frozen");
        Directory.CreateDirectory(frozen, ProtectedFiles.PrivateDirectory);
        string digest;
        using (var output = new SafeDirectory(frozen))
        {
            foreach (var member in new[] { name, name + ".sha256" })
            {
                using var original = input.Read(member);
                var limit = member.EndsWith(".sha256") ? 512 : ArchiveContract.ByteLimit;
                if (original.Length > limit) throw new IOException("Frozen input exceeds limit.");
                using var copy = output.Write(member);
                var buffer = new byte[65536];
                long total = 0;
                int count;
                while ((count = await original.ReadAsync(buffer, token)) != 0)
                {
                    total += count;
                    if (total > limit) throw new IOException("Frozen input grew beyond limit.");
                    await copy.WriteAsync(buffer.AsMemory(0, count), token);
                }
                copy.Flush(true);
            }
            using var archive = output.Read(name);
            digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(archive, token));
            output.Flush();
        }
        var expected = TargetEvidence(config, options);
        var payload = options.Get("--restore-payload");
        var payloadFingerprint = BackupPolicy.Fingerprint(payload);
        new BackupPolicy { Payload = payload, PayloadSha256 = payloadFingerprint }.CheckPayload();
        ProtectedFiles.Create(Path.Combine(directory, "source.json"), JsonSerializer.Serialize(expected), 0, 1654);
        ProtectedFiles.Create(Path.Combine(directory, "payload"), payload);
        foreach (var member in new[] { name, name + ".sha256" })
        {
            File.SetUnixFileMode(Path.Combine(frozen, member), ProtectedFiles.PrivateFile | UnixFileMode.GroupRead);
            if (chown(Path.Combine(frozen, member), 0, 1654) != 0) throw new IOException("Frozen ownership failed.");
        }
        File.SetUnixFileMode(frozen, ProtectedFiles.PrivateDirectory | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        if (chown(frozen, 0, 1654) != 0) throw new IOException("Frozen ownership failed.");
        await VerifyAsync(config, directory, operation, payload, name, source, token);
        using var manifestFile = File.OpenRead(Path.Combine(directory, "verified", "manifest.json"));
        var manifest = ArchiveContract.ReadManifest(manifestFile);
        await CheckImageAsync(config, expected, token);
        var plan = new RestorePlan(operation, root, config, source, manifest.Archive, digest, manifest.Completed,
            manifest.Mode, expected.BundleFingerprint, expected.PayloadFingerprint, payloadFingerprint,
            config.StorageGeneration, Guid.NewGuid().ToString("N"), options.Has("--new-install"),
            options.Has("--without-emergency-backup"), options.Has("--archive"))
        { LocalSecretsFingerprint = options.Has("--new-install") ? null : ProtectedFiles.SecretsFingerprint(root) };
        ProtectedFiles.Create(Path.Combine(directory, "plan.json"), JsonSerializer.Serialize(plan));
        foreach (var path in new[] { directory, Path.GetDirectoryName(directory)!, root })
        {
            using var parent = new SafeDirectory(path);
            parent.Flush();
        }
        return plan;
    }

    /// <summary>Capture evidence is independent of the restore payload and never comes from archive-selected files.</summary>
    private static SourceIdentity TargetEvidence(Deployment config, RestoreOptions options)
    {
        SourceIdentity expected;
        if (options.Has("--target-evidence"))
        {
            var path = options.Get("--target-evidence");
            ProtectedFiles.SafePath(path);
            using var parent = new SafeDirectory(Path.GetDirectoryName(path)!);
            using var file = parent.Read(Path.GetFileName(path));
            if (file.Length > ArchiveContract.ManifestLimit) throw new UsageException("Target evidence exceeds bound.");
            expected = JsonSerializer.Deserialize<SourceIdentity>(file, ArchiveContract.Json) ?? throw new UsageException("Missing target evidence.");
        }
        else expected = config.Backup?.Source ?? throw new UsageException("Independent --target-evidence is required.");
        ArchiveContract.ValidateSource(expected);
        var capture = options.Has("--capture-payload") ? options.Get("--capture-payload") : config.Backup?.Payload
            ?? throw new UsageException("Trusted capture payload evidence is required.");
        if (expected.Kind != "compose" || expected.PayloadFingerprint != BackupPolicy.Fingerprint(capture) ||
            expected.BundleFingerprint != BackupConfiguration.BundleFingerprint(config) ||
            expected.ApplicationImage != "ghcr.io/stef-k/wayfarer@" + config.AppDigest ||
            expected.DatabaseImage != "ghcr.io/stef-k/wayfarer-db@" + config.DbDigest)
            throw new UsageException("Trusted target bundle/images/capture evidence disagree.");
        return expected;
    }

    /// <summary>Inspect actual local image identity; never pull or infer platform/revision from a digest-shaped string.</summary>
    private async Task CheckImageAsync(Deployment config, SourceIdentity expected, CancellationToken token)
    {
        var containers = new RestoreContainers(runner);
        foreach (var image in new[] { expected.ApplicationImage, expected.DatabaseImage })
        {
            using var document = JsonDocument.Parse(await containers.Required(["image", "inspect", image], token));
            var actual = document.RootElement[0];
            if (actual.GetProperty("Os").GetString() != "linux" || actual.GetProperty("Architecture").GetString() != "amd64")
                throw new UsageException("Local image platform mismatch.");
            if (image == expected.ApplicationImage &&
                actual.GetProperty("Config").GetProperty("Labels").GetProperty("org.opencontainers.image.revision").GetString() != expected.SourceRevision)
                throw new UsageException("Local application source revision mismatch.");
        }
    }

    /// <summary>All parser writes are bounded operation storage, with no source volume, DB secret or network access.</summary>
    public async Task VerifyAsync(Deployment config, string directory, Guid operation, string payload, string name, Guid source, CancellationToken token)
    {
        var staging = Path.Combine(directory, "verified");
        if (Directory.Exists(staging)) Directory.Move(staging, staging + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging, ProtectedFiles.PrivateDirectory);
        if (chown(staging, 1654, 1654) != 0) throw new IOException("Staging ownership failed.");
        await new RestoreContainers(runner).RunAsync(config.Project + "-restore-verify-" + operation.ToString("N") + "-" + Guid.NewGuid().ToString("N"),
            [.. RestoreContainers.Unprivileged(), "--network=none", "--volume", payload + ":/worker:ro",
                "--volume", Path.Combine(directory, "frozen") + ":/frozen:ro",
                "--volume", Path.Combine(directory, "source.json") + ":/target/source.json:ro",
                "--volume", staging + ":/staging:rw", "--entrypoint=/worker", "ghcr.io/stef-k/wayfarer-db@" + config.DbDigest,
                "restore-verify", name, source.ToString("D")], token);
    }
}
