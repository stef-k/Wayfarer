using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Immutable release reference retained by installation and operation owners.</summary>
public sealed record ReleaseAuthority(string Name, string Fingerprint, string OperatorVersion, string OperatorSha256)
{
    /// <summary>Bind an exact validated release, independent of its containing deployment root.</summary>
    public static ReleaseAuthority From(ReleaseBundle bundle) => new(bundle.Manifest.Name, bundle.Fingerprint,
        bundle.Manifest.Operator.Version, bundle.Manifest.Files.Single(file => file.Path == "wayfarerctl").Sha256);

    /// <summary>Only generated release names are valid local selectors.</summary>
    public void Validate()
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(Name,
                "\\A(?:v[0-9]+\\.[0-9]+\\.[0-9]+|candidate-v[0-9]+\\.[0-9]+\\.[0-9]+-[a-f0-9]{40})\\z") ||
            Name.Length > 100 || !ReleaseContract.Hash(Fingerprint) || !ReleaseContract.Hash(OperatorSha256) ||
            !ReleaseContract.VersionSyntax(OperatorVersion)) throw new IOException("Invalid release authority.");
    }
}

/// <summary>Placement is local-only and never changes application/configuration authority.</summary>
public static class ReleaseStore
{
    /// <summary>Copy only validated handles into private staging, revalidate, then publish without replacement.</summary>
    public static ReleaseBundle Import(string root, string source)
    {
        ProtectedFiles.RequireRoot();
        ProtectedFiles.SafePath(root);
        ProtectedFiles.Check(root, 0, directory: true);
        var candidate = ReleaseBundle.Validate(source);
        var releases = Path.Combine(root, "releases");
        Directory.CreateDirectory(releases, ProtectedFiles.PrivateDirectory);
        ProtectedFiles.Check(releases, 0, directory: true);
        using var destination = new SafeDirectory(releases);
        destination.RequireLocalControl();
        var final = Path.Combine(releases, candidate.Manifest.Name);
        if (destination.Names().Contains(candidate.Manifest.Name)) return Same(final, candidate.Fingerprint);
        var stageName = ".stage-" + Guid.NewGuid().ToString("N");
        var stage = Path.Combine(releases, stageName);
        Directory.CreateDirectory(stage, ProtectedFiles.PrivateDirectory);
        // Incomplete stages have no authority. A fixed receipt outside payload documents their sole owner.
        ProtectedFiles.Create(Path.Combine(releases, stageName + ".json"), JsonSerializer.Serialize(ReleaseAuthority.From(candidate)));
        destination.Flush();
        using var original = new SafeDirectory(source);
        foreach (var name in ReleaseContract.Directories)
            Directory.CreateDirectory(Path.Combine(stage, name), ProtectedFiles.PrivateDirectory);
        using (var target = new SafeDirectory(stage))
        {
            foreach (var name in ReleaseContract.Payloads.Append("release.json"))
            {
                using var input = original.Read(name);
                using var output = target.Write(name);
                var limit = name == "release.json" ? ArchiveContract.ManifestLimit : 256L * 1024 * 1024;
                Copy(input, output, limit);
                output.Flush(true);
                File.SetUnixFileMode(Path.Combine(stage, name), (UnixFileMode)ReleaseContract.Mode(name));
            }
            foreach (var name in ReleaseContract.Directories)
            {
                using var child = target.Child(name);
                child.Flush();
            }
            target.Flush();
        }
        Same(stage, candidate.Fingerprint);
        destination.Publish(stageName, candidate.Manifest.Name);
        destination.Delete(stageName + ".json");
        return Same(final, candidate.Fingerprint);
    }

    /// <summary>Reconcile only a fully validated receipted stage; partial stages remain evidence, never auto-deleted.</summary>
    public static ReleaseBundle Reconcile(string root, string stageName)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(stageName, "\\A\\.stage-[a-f0-9]{32}\\z"))
            throw new IOException("Invalid placement identity.");
        var releases = Path.Combine(root, "releases");
        ProtectedFiles.SafePath(releases);
        ProtectedFiles.Check(releases, 0, directory: true);
        using var parent = new SafeDirectory(releases);
        var receiptPath = Path.Combine(releases, stageName + ".json");
        ProtectedFiles.Check(receiptPath, 0);
        using var receipt = parent.Read(stageName + ".json");
        if (receipt.Length > 1024) throw new IOException("Placement receipt exceeds bound.");
        var authority = JsonSerializer.Deserialize<ReleaseAuthority>(receipt, ArchiveContract.Json)
            ?? throw new IOException("Missing placement receipt.");
        authority.Validate();
        if (parent.Names().Contains(authority.Name)) return Select(root, authority);
        var staged = Same(Path.Combine(releases, stageName), authority.Fingerprint);
        if (ReleaseAuthority.From(staged) != authority) throw new IOException("Placement owner differs.");
        parent.Publish(stageName, authority.Name);
        parent.Delete(stageName + ".json");
        return Select(root, authority);
    }

    /// <summary>Retained selection never trusts directory placement alone.</summary>
    public static ReleaseBundle Select(string root, ReleaseAuthority authority)
    {
        authority.Validate();
        var bundle = Same(Path.Combine(root, "releases", authority.Name), authority.Fingerprint);
        if (ReleaseAuthority.From(bundle) != authority) throw new IOException("Retained operator owner mismatch.");
        return bundle;
    }

    private static ReleaseBundle Same(string path, string fingerprint)
    {
        var bundle = ReleaseBundle.Validate(path, installed: true);
        if (bundle.Fingerprint != fingerprint) throw new IOException("Immutable release collision or corruption.");
        return bundle;
    }

    private static void Copy(Stream input, Stream output, long limit)
    {
        var buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            total += count;
            if (total > limit) throw new IOException("Release payload exceeds bound.");
            output.Write(buffer, 0, count);
        }
    }
}
