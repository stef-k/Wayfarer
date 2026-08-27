using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddLocationEnrichmentWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnrichmentRequested",
                table: "LocationImports",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "EnrichmentRequestedAtUtc",
                table: "LocationImports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LocationEnrichmentWorkflows",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IntentEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Epoch = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProcessedCount = table.Column<int>(type: "integer", nullable: false),
                    EnrichedCount = table.Column<int>(type: "integer", nullable: false),
                    SkippedCount = table.Column<int>(type: "integer", nullable: false),
                    RetryableDeferredCount = table.Column<int>(type: "integer", nullable: false),
                    PermanentlyDeferredCount = table.Column<int>(type: "integer", nullable: false),
                    RemainingEligibleCount = table.Column<int>(type: "integer", nullable: false),
                    AdmittedUsageCount = table.Column<int>(type: "integer", nullable: false),
                    NextEligibleAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocationEnrichmentWorkflows", x => x.UserId);
                    table.CheckConstraint("CK_LocationEnrichmentWorkflow_Counters", "\"ProcessedCount\" >= 0 AND \"EnrichedCount\" >= 0 AND \"SkippedCount\" >= 0 AND \"RetryableDeferredCount\" >= 0 AND \"PermanentlyDeferredCount\" >= 0 AND \"RemainingEligibleCount\" >= 0 AND \"AdmittedUsageCount\" >= 0");
                    table.CheckConstraint("CK_LocationEnrichmentWorkflow_Epoch", "\"Epoch\" >= 0");
                    table.ForeignKey(
                        name: "FK_LocationEnrichmentWorkflows_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LocationEnrichmentAttempts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CredentialGeneration = table.Column<int>(type: "integer", nullable: false),
                    ConfigurationGeneration = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AdmittedAttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocationEnrichmentAttempts", x => x.Id);
                    table.CheckConstraint("CK_LocationEnrichmentAttempt_Count", "\"AdmittedAttemptCount\" >= 0 AND \"AdmittedAttemptCount\" <= 3");
                    table.ForeignKey(
                        name: "FK_LocationEnrichmentAttempts_LocationEnrichmentWorkflows_User~",
                        column: x => x.UserId,
                        principalTable: "LocationEnrichmentWorkflows",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LocationEnrichmentAttempts_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LocationEnrichmentAttempts_LocationId",
                table: "LocationEnrichmentAttempts",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_LocationEnrichmentAttempts_UserId_LocationId",
                table: "LocationEnrichmentAttempts",
                columns: new[] { "UserId", "LocationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LocationEnrichmentAttempts_UserId_NextAttemptAtUtc",
                table: "LocationEnrichmentAttempts",
                columns: new[] { "UserId", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LocationEnrichmentWorkflow_Due",
                table: "LocationEnrichmentWorkflows",
                columns: new[] { "State", "NextEligibleAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LocationEnrichmentAttempts");

            migrationBuilder.DropTable(
                name: "LocationEnrichmentWorkflows");

            migrationBuilder.DropColumn(
                name: "EnrichmentRequested",
                table: "LocationImports");

            migrationBuilder.DropColumn(
                name: "EnrichmentRequestedAtUtc",
                table: "LocationImports");
        }
    }
}
