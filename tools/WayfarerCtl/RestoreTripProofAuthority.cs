namespace WayfarerCtl;

/// <summary>Establishes new Trip proof continuity while the restored database is the only running candidate service.</summary>
internal static class RestoreTripProofAuthority
{
    /// <summary>Uses the persisted random restore UUID on retries; after writes are possible only verifies its presence.</summary>
    internal static async Task PrepareAsync(RestoreContainers owner, string[] databaseExec, RestoreReceipt receipt, CancellationToken token)
    {
        var sql = BuildSql(receipt.Plan.Operation, receipt.Phase == RestorePhase.ActivatedStopped);
        await owner.Required([.. databaseExec, "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "wayfarer", "-c", sql], token);
    }

    /// <summary>Returns the database activation step, executed before any candidate writer can start.</summary>
    internal static string BuildSql(Guid authority, bool rotate)
    {
        var rotation = rotate
            ? $"""
                UPDATE public.trip_metadata_proof_authority
                SET authority_id = '{authority:D}', retired_through = clock_timestamp()
                WHERE singleton AND authority_id <> '{authority:D}';
                """
            : "";
        return $$"""
            DO $restore$
            BEGIN
                IF to_regclass('public.trip_metadata_proof_authority') IS NULL AND NOT EXISTS (
                    SELECT 1 FROM public."__EFMigrationsHistory" WHERE "MigrationId" = '20261127120000_TripMetadataReceipts'
                ) THEN
                    RETURN;
                END IF;
                {{rotation}}
                IF to_regclass('public.trip_metadata_receipts') IS NULL OR
                    NOT EXISTS (SELECT 1 FROM public.trip_metadata_proof_authority WHERE singleton AND authority_id = '{{authority:D}}') THEN
                    RAISE EXCEPTION 'Restored Trip proof authority is unavailable';
                END IF;
            END
            $restore$;
            """;
    }
}
