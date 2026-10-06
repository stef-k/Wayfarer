namespace WayfarerCtl;

/// <summary>Normal removal preserves every volume; purge is a separate plan-authorized choice.</summary>
public enum UninstallMode { Normal, Purge }

/// <summary>Backup capture and verification precede destructive intent; waiver is explicit hashed authority.</summary>
public enum UninstallBackup { VerifiedQuiesced, Waived }

/// <summary>Only planning may select mode/backup policy; acceptance carries exactly one protected plan hash.</summary>
public sealed record UninstallOptions(bool Plan, UninstallMode Mode, UninstallBackup? Backup, string? Accept)
{
    /// <summary>Reject repeated, unknown or conflicting options without touching installation state.</summary>
    public static UninstallOptions Parse(string[] args)
    {
        if (args is ["--accept-plan", var hash] && ReleaseContract.Hash(hash))
            return new(false, UninstallMode.Normal, null, hash);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var argument in args)
        {
            if (argument is not ("--plan" or "--purge" or "--backup" or "--without-backup") || !flags.Add(argument))
                throw new UsageException("Unknown, duplicate or conflicting uninstall option.");
        }
        if (!flags.Contains("--plan") || flags.Contains("--backup") && flags.Contains("--without-backup"))
            throw new UsageException("Use uninstall [--purge] --plan --backup|--without-backup, or --accept-plan SHA256.");
        return new(true, flags.Contains("--purge") ? UninstallMode.Purge : UninstallMode.Normal,
            flags.Contains("--backup") ? UninstallBackup.VerifiedQuiesced :
            flags.Contains("--without-backup") ? UninstallBackup.Waived : null, null);
    }

    /// <summary>Only an interactive command layer may resolve an omitted backup choice before constructing a plan.</summary>
    public void CheckInteraction(bool interactive)
    {
        if (Plan && Backup is null && !interactive)
            throw new UsageException("Redirected uninstall planning requires --backup or --without-backup.");
    }
}
