using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class CompleteLocationEnrichmentAuthorityConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_LocationEnrichmentWorkflow_ExecutionLeasePair",
                table: "LocationEnrichmentWorkflows");

            migrationBuilder.AddColumn<long>(
                name: "OperationFencingGeneration",
                table: "LocationEnrichmentAttempts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OperationId",
                table: "LocationEnrichmentAttempts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OperationStartedAtUtc",
                table: "LocationEnrichmentAttempts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_LocationImport_EnrichmentPauseReason",
                table: "LocationImports",
                sql: "\"EnrichmentPauseReason\" IS NULL OR \"EnrichmentPauseReason\" IN ('CredentialRequired','NoProviderSelected','ConsentRequired','Unauthorized','VerificationRequired','Exhausted','StaleAuthority')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LocationImport_RemainingEnrichment",
                table: "LocationImports",
                sql: "\"RemainingEnrichmentCount\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LocationEnrichmentWorkflow_ExecutionLeasePair",
                table: "LocationEnrichmentWorkflows",
                sql: "(\"ExecutionLeaseId\" IS NULL AND \"ExecutionLeaseExpiresAtUtc\" IS NULL) OR (\"ExecutionLeaseId\" IS NOT NULL AND \"ExecutionLeaseExpiresAtUtc\" IS NOT NULL AND \"ExecutionFencingGeneration\" > 0 AND \"State\" = 'Running' AND \"IntentEnabled\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LocationEnrichmentAttempt_OperationPair",
                table: "LocationEnrichmentAttempts",
                sql: "(\"OperationId\" IS NULL AND \"OperationFencingGeneration\" IS NULL AND \"OperationStartedAtUtc\" IS NULL) OR (\"OperationId\" IS NOT NULL AND \"OperationFencingGeneration\" > 0 AND \"OperationStartedAtUtc\" IS NOT NULL AND \"NextAttemptAtUtc\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_LocationImport_EnrichmentPauseReason",
                table: "LocationImports");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LocationImport_RemainingEnrichment",
                table: "LocationImports");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LocationEnrichmentWorkflow_ExecutionLeasePair",
                table: "LocationEnrichmentWorkflows");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LocationEnrichmentAttempt_OperationPair",
                table: "LocationEnrichmentAttempts");

            migrationBuilder.DropColumn(
                name: "OperationFencingGeneration",
                table: "LocationEnrichmentAttempts");

            migrationBuilder.DropColumn(
                name: "OperationId",
                table: "LocationEnrichmentAttempts");

            migrationBuilder.DropColumn(
                name: "OperationStartedAtUtc",
                table: "LocationEnrichmentAttempts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LocationEnrichmentWorkflow_ExecutionLeasePair",
                table: "LocationEnrichmentWorkflows",
                sql: "(\"ExecutionLeaseId\" IS NULL) = (\"ExecutionLeaseExpiresAtUtc\" IS NULL)");
        }
    }
}
