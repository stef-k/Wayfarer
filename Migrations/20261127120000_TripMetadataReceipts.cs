using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Wayfarer.Models;

namespace Wayfarer.Migrations;

/// <summary>Creates the SQL-owned Trip operation fence evidence and continuous database authority.</summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261127120000_TripMetadataReceipts")]
public sealed class TripMetadataReceipts : Migration
{
    /// <summary>Stores only bounded result evidence and uses the account FK for erasure.</summary>
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE public.trip_metadata_proof_authority (
            singleton boolean PRIMARY KEY CHECK (singleton),
            authority_id uuid NOT NULL,
            retired_through timestamptz NOT NULL
        );
        INSERT INTO public.trip_metadata_proof_authority VALUES (true, gen_random_uuid(), clock_timestamp());

        CREATE TABLE public.trip_metadata_receipts (
            subject_id text NOT NULL REFERENCES public."AspNetUsers" ("Id") ON DELETE CASCADE,
            operation_id uuid NOT NULL,
            authority_id uuid NOT NULL,
            execute_before timestamptz NOT NULL,
            retain_until timestamptz NOT NULL,
            correlation jsonb NOT NULL,
            evidence jsonb NOT NULL,
            PRIMARY KEY (subject_id, operation_id)
        );
        CREATE INDEX trip_metadata_receipts_retention_idx ON public.trip_metadata_receipts (retain_until, operation_id, subject_id);
        """);

    /// <summary>Removes the authority and the corresponding evidence together.</summary>
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TABLE public.trip_metadata_receipts;
        DROP TABLE public.trip_metadata_proof_authority;
        """);
}
