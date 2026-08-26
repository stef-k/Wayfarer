using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class MakeLocationEnrichmentAttemptTimeNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_LocationEnrichmentAttempt_OperationPair",
                table: "LocationEnrichmentAttempts");

            migrationBuilder.Sql("""
                UPDATE "LocationEnrichmentAttempts"
                SET "LastAttemptAtUtc" = TIMESTAMPTZ '1970-01-01 00:00:00+00'
                WHERE "LastAttemptAtUtc" IS NULL;
                """);

            migrationBuilder.AlterColumn<DateTime>(
                name: "LastAttemptAtUtc",
                table: "LocationEnrichmentAttempts",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LocationEnrichmentAttempt_OperationPair",
                table: "LocationEnrichmentAttempts",
                sql: "(\"OperationId\" IS NULL AND \"OperationLeaseId\" IS NULL AND \"OperationFencingGeneration\" IS NULL AND \"OperationStartedAtUtc\" IS NULL AND \"OperationWorkflowEpoch\" IS NULL AND \"OperationAttemptNumber\" IS NULL) OR (\"OperationId\" IS NOT NULL AND \"OperationLeaseId\" IS NOT NULL AND \"OperationFencingGeneration\" > 0 AND \"OperationStartedAtUtc\" IS NOT NULL AND \"OperationWorkflowEpoch\" >= 0 AND \"LastAttemptAtUtc\" IS NOT NULL AND \"OperationAttemptNumber\" > 0 AND \"OperationAttemptNumber\" = \"AdmittedAttemptCount\" AND \"NextAttemptAtUtc\" IS NOT NULL AND \"ProviderProfileId\" IS NOT NULL AND \"Capability\" = 1 AND \"ProviderKey\" IN ('geoapify', 'mapbox') AND \"CredentialGeneration\" > 0 AND \"ConfigurationGeneration\" > 0 AND \"SelectionGeneration\" > 0 AND \"Verification\" = 1 AND \"VerificationCredentialGeneration\" > 0 AND \"VerificationGeneration\" > 0 AND ((\"ProviderKey\" = 'geoapify' AND \"ConsentVersion\" IS NULL AND \"ConsentTimestamp\" IS NULL AND \"ConsentCredentialGeneration\" IS NULL) OR (\"ProviderKey\" = 'mapbox' AND \"ConsentVersion\" > 0 AND \"ConsentTimestamp\" IS NOT NULL AND \"ConsentCredentialGeneration\" > 0))) IS TRUE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_LocationEnrichmentAttempt_OperationPair",
                table: "LocationEnrichmentAttempts");

            migrationBuilder.AlterColumn<DateTime>(
                name: "LastAttemptAtUtc",
                table: "LocationEnrichmentAttempts",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_LocationEnrichmentAttempt_OperationPair",
                table: "LocationEnrichmentAttempts",
                sql: "(\"OperationId\" IS NULL AND \"OperationLeaseId\" IS NULL AND \"OperationFencingGeneration\" IS NULL AND \"OperationStartedAtUtc\" IS NULL AND \"OperationWorkflowEpoch\" IS NULL AND \"OperationAttemptNumber\" IS NULL) OR (\"OperationId\" IS NOT NULL AND \"OperationLeaseId\" IS NOT NULL AND \"OperationFencingGeneration\" > 0 AND \"OperationStartedAtUtc\" IS NOT NULL AND \"OperationWorkflowEpoch\" >= 0 AND \"OperationAttemptNumber\" > 0 AND \"OperationAttemptNumber\" = \"AdmittedAttemptCount\" AND \"NextAttemptAtUtc\" IS NOT NULL AND \"ProviderProfileId\" IS NOT NULL AND \"Capability\" = 1 AND \"ProviderKey\" IN ('geoapify', 'mapbox') AND \"CredentialGeneration\" > 0 AND \"ConfigurationGeneration\" > 0 AND \"SelectionGeneration\" > 0 AND \"Verification\" = 1 AND \"VerificationCredentialGeneration\" > 0 AND \"VerificationGeneration\" > 0 AND ((\"ProviderKey\" = 'geoapify' AND \"ConsentVersion\" IS NULL AND \"ConsentTimestamp\" IS NULL AND \"ConsentCredentialGeneration\" IS NULL) OR (\"ProviderKey\" = 'mapbox' AND \"ConsentVersion\" > 0 AND \"ConsentTimestamp\" IS NOT NULL AND \"ConsentCredentialGeneration\" > 0))) IS TRUE");
        }
    }
}
