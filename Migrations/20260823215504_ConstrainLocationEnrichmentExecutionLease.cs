using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class ConstrainLocationEnrichmentExecutionLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_LocationEnrichmentWorkflow_Counters",
                table: "LocationEnrichmentWorkflows");

            migrationBuilder.AddColumn<long>(
                name: "ExecutionFencingGeneration",
                table: "LocationEnrichmentWorkflows",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExecutionLeaseExpiresAtUtc",
                table: "LocationEnrichmentWorkflows",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ExecutionLeaseId",
                table: "LocationEnrichmentWorkflows",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FailedBatchCount",
                table: "LocationEnrichmentWorkflows",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_LocationEnrichmentWorkflow_Counters",
                table: "LocationEnrichmentWorkflows",
                sql: "\"ProcessedCount\" >= 0 AND \"EnrichedCount\" >= 0 AND \"SkippedCount\" >= 0 AND \"RetryableDeferredCount\" >= 0 AND \"PermanentlyDeferredCount\" >= 0 AND \"RemainingEligibleCount\" >= 0 AND \"AdmittedUsageCount\" >= 0 AND \"FailedBatchCount\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LocationEnrichmentWorkflow_ExecutionFence",
                table: "LocationEnrichmentWorkflows",
                sql: "\"ExecutionFencingGeneration\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LocationEnrichmentWorkflow_ExecutionLeasePair",
                table: "LocationEnrichmentWorkflows",
                sql: "(\"ExecutionLeaseId\" IS NULL) = (\"ExecutionLeaseExpiresAtUtc\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LocationEnrichmentWorkflow_Outcome",
                table: "LocationEnrichmentWorkflows",
                sql: "\"Outcome\" IN ('None','NoCandidates','BudgetExhausted','AuthorityUnavailable','RetryableFailure','InvalidCoordinates','NoResult','AttemptLimit','DataFailure')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LocationEnrichmentWorkflow_State",
                table: "LocationEnrichmentWorkflows",
                sql: "\"State\" IN ('Idle','Scheduled','Running','PausedByUser','PausedByBudget','PausedByAuthority','BackingOff','Completed','Cancelled','Failed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_LocationEnrichmentWorkflow_Counters",
                table: "LocationEnrichmentWorkflows");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LocationEnrichmentWorkflow_ExecutionFence",
                table: "LocationEnrichmentWorkflows");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LocationEnrichmentWorkflow_ExecutionLeasePair",
                table: "LocationEnrichmentWorkflows");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LocationEnrichmentWorkflow_Outcome",
                table: "LocationEnrichmentWorkflows");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LocationEnrichmentWorkflow_State",
                table: "LocationEnrichmentWorkflows");

            migrationBuilder.DropColumn(
                name: "ExecutionFencingGeneration",
                table: "LocationEnrichmentWorkflows");

            migrationBuilder.DropColumn(
                name: "ExecutionLeaseExpiresAtUtc",
                table: "LocationEnrichmentWorkflows");

            migrationBuilder.DropColumn(
                name: "ExecutionLeaseId",
                table: "LocationEnrichmentWorkflows");

            migrationBuilder.DropColumn(
                name: "FailedBatchCount",
                table: "LocationEnrichmentWorkflows");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LocationEnrichmentWorkflow_Counters",
                table: "LocationEnrichmentWorkflows",
                sql: "\"ProcessedCount\" >= 0 AND \"EnrichedCount\" >= 0 AND \"SkippedCount\" >= 0 AND \"RetryableDeferredCount\" >= 0 AND \"PermanentlyDeferredCount\" >= 0 AND \"RemainingEligibleCount\" >= 0 AND \"AdmittedUsageCount\" >= 0");
        }
    }
}
