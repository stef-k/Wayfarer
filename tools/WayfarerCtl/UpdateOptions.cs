namespace WayfarerCtl;

/// <summary>Exact update grammar has no bypass, network resolver, or implicit non-interactive authorization.</summary>
public sealed record UpdateOptions(string? Bundle = null, bool Plan = false, string? Accept = null,
    Guid? Resume = null, Guid? Abort = null, Guid? Restore = null)
{
    public static UpdateOptions Parse(string[] args)
    {
        if (args is ["--bundle", var bundle, "--plan"])
        {
            BackupPolicy.LiteralPath(bundle);
            return new(Bundle: bundle, Plan: true);
        }
        if (args is ["--accept-plan", var hash] && ReleaseContract.Hash(hash)) return new(Accept: hash);
        if (args is [var action, var id] && Guid.TryParseExact(id, "D", out var operation) && operation != Guid.Empty)
            return action switch
            {
                "--resume" => new(Resume: operation),
                "--abort" => new(Abort: operation),
                "--restore" => new(Restore: operation),
                _ => throw new UsageException("Unknown update recovery action.")
            };
        throw new UsageException("Use update --bundle /trusted/bundle --plan, --accept-plan SHA256, or --resume|--abort|--restore OPERATION.");
    }

    /// <summary>Candidate execution exists only in explicitly compiled disposable qualification operators.</summary>
    internal static bool QualificationCandidates(string root, string project)
    {
#if UPDATE_QUALIFICATION
        return root.StartsWith("/tmp/wayfarer-533-", StringComparison.Ordinal) &&
            System.Text.RegularExpressions.Regex.IsMatch(project, "^wayfarer-648-[a-f0-9]{10}$");
#else
        return false;
#endif
    }

    /// <summary>Ordering does not grant compatibility: the exact retained source fingerprint must be declared.</summary>
    public static ReleaseSourceBoundary Boundary(ReleaseBundle current, ReleaseBundle target, bool candidates = false)
    {
        var source = current.Manifest;
        var next = target.Manifest;
        if ((!candidates && (source.Status != "stable" || next.Status != "stable")) ||
            Version.Parse(next.Version) <= Version.Parse(source.Version) ||
            source.Images.ApplicationDigest == next.Images.ApplicationDigest ||
            source.Images.DatabaseDigest != next.Images.DatabaseDigest || source.Images.CaddyDigest != next.Images.CaddyDigest)
            throw new UsageException("Update requires a later supported release and unchanged DB/proxy authority.");
        var boundary = next.Sources.SingleOrDefault(value => value.Fingerprint == current.Fingerprint && value.Version == source.Version)
            ?? throw new UsageException("Target does not authorize this exact source release.");
        if (!boundary.ExactOrderedPrefix || boundary.ReferenceSeeding || boundary.TerminalMigration != source.Application.TerminalMigration ||
            !next.Application.Migrations.Take(source.Application.Migrations.Length).SequenceEqual(source.Application.Migrations))
            throw new UsageException("Unsupported migration prefix or reference seeding boundary.");
        ReleaseContract.RequireUse(next, ReleaseCommands.OperatorVersion);
        if (!ReleaseContract.CurrentOperator.UpdateReceiptSchemas.Contains(1) || !next.Operator.UpdateReceiptSchemas.Contains(1))
            throw new UsageException("Operator cannot own this update receipt protocol.");
        return boundary;
    }
}
