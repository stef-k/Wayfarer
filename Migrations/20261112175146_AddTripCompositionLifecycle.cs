using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddTripCompositionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TripCompositions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SourceAId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceBId = table.Column<Guid>(type: "uuid", nullable: true),
                    SnapshotA = table.Column<string>(type: "jsonb", nullable: true),
                    SnapshotB = table.Column<string>(type: "jsonb", nullable: true),
                    Mappings = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripCompositions", x => x.Id);
                    table.CheckConstraint("CK_TripComposition_Lifecycle", "(\"FinishedAt\" IS NULL AND \"SnapshotA\" IS NOT NULL AND \"SnapshotB\" IS NOT NULL AND \"Mappings\" IS NOT NULL) OR (\"FinishedAt\" IS NOT NULL AND \"SnapshotA\" IS NULL AND \"SnapshotB\" IS NULL AND \"Mappings\" IS NULL AND \"SourceAId\" IS NULL AND \"SourceBId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_TripCompositions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TripCompositions_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TripOrigins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceTripId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChangeNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripOrigins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TripOrigins_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TripCompositions_SourceAId_SourceBId",
                table: "TripCompositions",
                columns: new[] { "SourceAId", "SourceBId" },
                filter: "\"FinishedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TripCompositions_TripId",
                table: "TripCompositions",
                column: "TripId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TripCompositions_UserId",
                table: "TripCompositions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TripOrigins_TripId",
                table: "TripOrigins",
                column: "TripId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TripCompositions");

            migrationBuilder.DropTable(
                name: "TripOrigins");
        }
    }
}
