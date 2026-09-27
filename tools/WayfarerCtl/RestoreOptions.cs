using System.Text.RegularExpressions;

namespace WayfarerCtl;

/// <summary>Strict restore grammar keeps provenance, verification and destructive authorization separate.</summary>
public sealed record RestoreOptions(Dictionary<string, string> Values, string? Basename)
{
    public bool Has(string name) => Values.ContainsKey(name);
    public string Get(string name) => Values.GetValueOrDefault(name) ?? throw new UsageException("Missing restore option " + name + ".");

    /// <summary>No generic yes, implicit redirected-input approval, option repetition or path-like basename.</summary>
    public static RestoreOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>();
        string? basename = null;
        var flags = new[] { "--plan", "--new-install", "--without-emergency-backup", "--trust-controlled-backup" };
        var valued = new[] { "--archive", "--source-installation", "--accept-plan", "--resume", "--abort",
            "--restore-payload", "--capture-payload", "--target-evidence", "--bundle", "--hostname", "--app-digest",
            "--db-digest", "--mode", "--project", "--edge-prefix", "--loopback-port" };
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (i == 0 && !key.StartsWith('-')) { ValidateBasename(key); basename = key; continue; }
            var value = flags.Contains(key) ? "true" : valued.Contains(key) && i + 1 < args.Length ? args[++i] :
                throw new UsageException("Invalid restore option.");
            if (!values.TryAdd(key, value)) throw new UsageException("Duplicate restore option.");
        }
        var result = new RestoreOptions(values, basename);
        if (result.Has("--resume") || result.Has("--abort"))
        {
            if (values.Count != 1 || basename is not null || !Guid.TryParseExact(values.Values.Single(), "D", out var operation) || operation == Guid.Empty)
                throw new UsageException("Restore recovery requires one exact operation UUID.");
            return result;
        }
        foreach (var key in new[] { "--archive", "--restore-payload", "--capture-payload", "--target-evidence", "--bundle" })
            if (values.TryGetValue(key, out var path)) BackupPolicy.LiteralPath(path);
        if (result.Has("--archive")) ValidateBasename(Path.GetFileName(result.Get("--archive")));
        if (basename is not null && result.Has("--archive") || result.Has("--new-install") && !result.Has("--archive") ||
            result.Has("--plan") && result.Has("--accept-plan")) throw new UsageException("Conflicting restore selection/authorization.");
        if (result.Has("--archive") && (!values.TryGetValue("--source-installation", out var source) ||
            !Guid.TryParseExact(source, "D", out var identity) || identity == Guid.Empty))
            throw new UsageException("External restore requires exact --source-installation acknowledgement.");
        if (result.Has("--accept-plan") && !Regex.IsMatch(result.Get("--accept-plan"), "^[a-f0-9]{64}$"))
            throw new UsageException("Invalid canonical plan hash.");
        return result;
    }

    /// <summary>Only the generated v1 publication namespace is selectable.</summary>
    public static void ValidateBasename(string name)
    {
        if (!Regex.IsMatch(name, @"\Awayfarer-recovery-v1_[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}_[0-9]{8}T[0-9]{13}Z_[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}\.tar\z"))
            throw new UsageException("Restore requires a generated v1 archive basename.");
    }
}
