using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Wayfarer.Models;

namespace Wayfarer.Migrations;

/// <summary>Creates the durable single-executor lane for metered Trip Place submissions.</summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261128120000_TripStructureExecutorClaims")]
public sealed class TripStructureExecutorClaims : Migration
{
    /// <summary>Stores the bound execution authority and only a digest of the claimant's private random nonce.</summary>
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE public.trip_structure_executor_claims (
            subject_id text NOT NULL REFERENCES public."AspNetUsers" ("Id") ON DELETE CASCADE,
            operation_id uuid NOT NULL,
            authority_id uuid NOT NULL,
            execute_before timestamptz NOT NULL,
            correlation jsonb NOT NULL,
            nonce_digest bytea NOT NULL CHECK (octet_length(nonce_digest) = 32),
            PRIMARY KEY (subject_id, operation_id)
        );
        CREATE INDEX trip_structure_claims_cutoff_idx
            ON public.trip_structure_executor_claims (execute_before, operation_id, subject_id);
        """);

    /// <summary>Removes the protected Place executor state.</summary>
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TABLE public.trip_structure_executor_claims;
        """);
}
