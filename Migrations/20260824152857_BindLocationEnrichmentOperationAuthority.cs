using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class BindLocationEnrichmentOperationAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Capability",
                table: "LocationEnrichmentAttempts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConsentCredentialGeneration",
                table: "LocationEnrichmentAttempts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConsentTimestamp",
                table: "LocationEnrichmentAttempts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConsentVersion",
                table: "LocationEnrichmentAttempts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OperationAttemptNumber",
                table: "LocationEnrichmentAttempts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OperationLeaseId",
                table: "LocationEnrichmentAttempts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OperationWorkflowEpoch",
                table: "LocationEnrichmentAttempts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProviderProfileId",
                table: "LocationEnrichmentAttempts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Verification",
                table: "LocationEnrichmentAttempts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VerificationCredentialGeneration",
                table: "LocationEnrichmentAttempts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VerificationGeneration",
                table: "LocationEnrichmentAttempts",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Capability",
                table: "LocationEnrichmentAttempts");

            migrationBuilder.DropColumn(
                name: "ConsentCredentialGeneration",
                table: "LocationEnrichmentAttempts");

            migrationBuilder.DropColumn(
                name: "ConsentTimestamp",
                table: "LocationEnrichmentAttempts");

            migrationBuilder.DropColumn(
                name: "ConsentVersion",
                table: "LocationEnrichmentAttempts");

            migrationBuilder.DropColumn(
                name: "OperationAttemptNumber",
                table: "LocationEnrichmentAttempts");

            migrationBuilder.DropColumn(
                name: "OperationLeaseId",
                table: "LocationEnrichmentAttempts");

            migrationBuilder.DropColumn(
                name: "OperationWorkflowEpoch",
                table: "LocationEnrichmentAttempts");

            migrationBuilder.DropColumn(
                name: "ProviderProfileId",
                table: "LocationEnrichmentAttempts");

            migrationBuilder.DropColumn(
                name: "Verification",
                table: "LocationEnrichmentAttempts");

            migrationBuilder.DropColumn(
                name: "VerificationCredentialGeneration",
                table: "LocationEnrichmentAttempts");

            migrationBuilder.DropColumn(
                name: "VerificationGeneration",
                table: "LocationEnrichmentAttempts");
        }
    }
}
