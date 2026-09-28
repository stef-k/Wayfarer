using System.Security.Cryptography;
using System.Text;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Validated immutable local bytes; callers re-open the authority before each use.</summary>
public sealed record ReleaseBundle(string Directory, ReleaseManifest Manifest, string Fingerprint)
{
    /// <summary>Reconstruct the release-level archive target solely from retained release bytes and a local project identity.</summary>
    public SourceIdentity Target(string project, bool currentCapture = false)
    {
        var capture = currentCapture ? null : Manifest.LegacyCapture;
        var result = new SourceIdentity
        {
            ApplicationVersion = Manifest.Application.CompiledVersion, SourceRevision = Manifest.SourceRevision,
            ReleaseStatus = capture?.ReleaseStatus ?? (Manifest.Status == "stable" ? "released" : "candidate"),
            ApplicationImage = "ghcr.io/stef-k/wayfarer@" + Manifest.Images.ApplicationDigest,
            DatabaseImage = "ghcr.io/stef-k/wayfarer-db@" + Manifest.Images.DatabaseDigest,
            BundleFingerprint = LegacyFingerprint(["compose.yaml", "external.yaml", "caddy/Caddyfile", "db/20-wayfarer.sh"]),
            PayloadFingerprint = LegacyFingerprint(capture is null
                ? ["wayfarer-recovery", "WayfarerRecoverySource.dll"] : ReleaseContract.CapturePayloads),
            Project = project, WorkerVersion = capture?.WorkerVersion ?? Manifest.Application.WorkerVersion,
            ExpectedMigrations = Manifest.Application.Migrations, ConfigurationSchema = 3,
            QuartzCompatibilityContract = Manifest.Application.QuartzCompatibilityContract,
            SupportedLegacySourceSchemas = Manifest.Application.SupportedLegacySourceSchemas,
            // Required capture field: zero denotes no observation in target-only evidence.
            // Restore ignores this field; installation snapshots never belong in a bundle.
            QuartzSnapshotFingerprint = new string('0', 32)
        };
        ArchiveContract.ValidateSource(result);
        return result;
    }

    private string LegacyFingerprint(string[] paths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths) hash.AppendData(Convert.FromHexString(Manifest.Files.Single(file => file.Path == path).Sha256));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Validate exact inventory, regular-file bytes and modes without Docker, DB or executable invocation.</summary>
    public static ReleaseBundle Validate(string directory, bool installed = false)
    {
        BackupPolicy.LiteralPath(directory);
        if (installed) ProtectedFiles.SafePath(directory);
        using var root = new SafeDirectory(directory);
        CheckDirectory(root, installed);
        using var manifestFile = root.Read("release.json");
        CheckFile(manifestFile, 420, installed, ArchiveContract.ManifestLimit);
        var manifestFacts = SafeDirectory.Inspect(manifestFile.SafeFileHandle);
        var manifest = ReleaseContract.Read(manifestFile);
        var expected = ReleaseContract.Inventory(manifest).Append("release.json").Order(StringComparer.Ordinal).ToArray();
        var actual = Inventory(root, "", installed, manifest).Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected)) throw new IOException("Unexpected or missing release content.");
        manifestFile.Position = 0;
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        fingerprint.AppendData(Encoding.UTF8.GetBytes("wayfarer-release:1:1\n"));
        fingerprint.AppendData(SHA256.HashData(manifestFile));
        if (!manifestFacts.Equals(SafeDirectory.Inspect(manifestFile.SafeFileHandle)))
            throw new IOException("Release manifest changed during validation.");
        foreach (var file in manifest.Files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            using var input = root.Read(file.Path);
            CheckFile(input, file.Mode, installed, 256L * 1024 * 1024);
            var before = SafeDirectory.Inspect(input.SafeFileHandle);
            var digest = SHA256.HashData(input);
            if (Convert.ToHexStringLower(digest) != file.Sha256 || !before.Equals(SafeDirectory.Inspect(input.SafeFileHandle)))
                throw new IOException("Release payload hash or inode changed.");
            fingerprint.AppendData(Encoding.UTF8.GetBytes(file.Path + "\n"));
            fingerprint.AppendData(digest);
        }
        return new ReleaseBundle(directory, manifest, Convert.ToHexStringLower(fingerprint.GetHashAndReset()));
    }

    private static IEnumerable<string> Inventory(SafeDirectory root, string prefix, bool installed, ReleaseManifest manifest)
    {
        foreach (var name in root.Names(16))
        {
            var path = prefix + name;
            if (ReleaseContract.Folders(manifest).Contains(path))
            {
                using var child = root.Child(name);
                CheckDirectory(child, installed);
                foreach (var file in Inventory(child, path + "/", installed, manifest)) yield return file;
            }
            else
            {
                if (!ReleaseContract.Inventory(manifest).Contains(path) && path != "release.json") throw new IOException("Unexpected release entry.");
                using var file = root.Read(name);
                yield return path;
            }
        }
    }

    private static void CheckDirectory(SafeDirectory directory, bool installed)
    {
        var facts = directory.Identity;
        if (!facts.IsDirectory || (facts.Mode & 0x12) != 0 || installed && (facts.User != 0 || facts.Group != 0))
            throw new IOException("Unsafe release directory ownership/mode.");
    }

    private static void CheckFile(FileStream file, int mode, bool installed, long limit)
    {
        var facts = SafeDirectory.Inspect(file.SafeFileHandle);
        if (!facts.IsFile || (facts.Mode & 0xfff) != mode || facts.Size > (ulong)limit ||
            installed && (facts.User != 0 || facts.Group != 0)) throw new IOException("Unsafe release payload type/mode/owner/size.");
    }

    /// <summary>Adopted runtime inputs must continue to match the retained authority on every load.</summary>
    public void Corroborate(Deployment config)
    {
        if (config.AppDigest != Manifest.Images.ApplicationDigest || config.DbDigest != Manifest.Images.DatabaseDigest)
            throw new IOException("Installation image identity contradicts release.");
        foreach (var path in ReleaseContract.Payloads.Take(5))
        {
            ProtectedFiles.SafePath(Path.Combine(config.Bundle, path));
            using var parent = new SafeDirectory(config.Bundle);
            using var file = parent.Read(path);
            if (Convert.ToHexStringLower(SHA256.HashData(file)) != Manifest.Files.Single(entry => entry.Path == path).Sha256)
                throw new IOException("Installation runtime inputs contradict release.");
        }
    }

    /// <summary>Legacy facts corroborate actual retained bytes; they never select or replace them.</summary>
    public void Corroborate(SourceIdentity evidence)
    {
        ArchiveContract.ValidateSource(evidence);
        var target = Target(evidence.Project, evidence.PayloadFingerprint == Target(evidence.Project, true).PayloadFingerprint);
        if (evidence.Kind != target.Kind || evidence.ApplicationVersion != target.ApplicationVersion ||
            evidence.SourceRevision != target.SourceRevision || evidence.ApplicationImage != target.ApplicationImage ||
            evidence.DatabaseImage != target.DatabaseImage || evidence.BundleFingerprint != target.BundleFingerprint ||
            evidence.PayloadFingerprint != target.PayloadFingerprint || evidence.WorkerVersion != target.WorkerVersion ||
            evidence.ReleaseStatus != target.ReleaseStatus ||
            (evidence.ConfigurationSchema == 2 ? !target.SupportedLegacySourceSchemas!.Contains(2) :
                evidence.QuartzCompatibilityContract != target.QuartzCompatibilityContract) ||
            !evidence.ExpectedMigrations.SequenceEqual(target.ExpectedMigrations))
            throw new IOException("Legacy source evidence contradicts retained release.");
    }
}
