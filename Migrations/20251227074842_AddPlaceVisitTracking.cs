using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaceVisitTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "VisitedAccuracyMultiplier",
                table: "ApplicationSettings",
                type: "double precision",
                nullable: false,
                defaultValue: 2.0);

            migrationBuilder.AddColumn<int>(
                name: "VisitedAccuracyRejectMeters",
                table: "ApplicationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 200);

            migrationBuilder.AddColumn<int>(
                name: "VisitedMaxRadiusMeters",
                table: "ApplicationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 100);

            migrationBuilder.AddColumn<int>(
                name: "VisitedMaxSearchRadiusMeters",
                table: "ApplicationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 150);

            migrationBuilder.AddColumn<int>(
                name: "VisitedMinRadiusMeters",
                table: "ApplicationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 35);

            migrationBuilder.AddColumn<int>(
                name: "VisitedPlaceNotesSnapshotMaxHtmlChars",
                table: "ApplicationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 20000);

            migrationBuilder.AddColumn<int>(
                name: "VisitedRequiredHits",
                table: "ApplicationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.CreateTable(
                name: "PlaceVisitCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    PlaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    FirstHitUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastHitUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsecutiveHits = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaceVisitCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaceVisitCandidates_Places_PlaceId",
                        column: x => x.PlaceId,
                        principalTable: "Places",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlaceVisitEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    PlaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArrivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TripIdSnapshot = table.Column<Guid>(type: "uuid", nullable: false),
                    TripNameSnapshot = table.Column<string>(type: "text", nullable: false),
                    RegionNameSnapshot = table.Column<string>(type: "text", nullable: false),
                    PlaceNameSnapshot = table.Column<string>(type: "text", nullable: false),
                    PlaceLocationSnapshot = table.Column<Point>(type: "geography(Point,4326)", nullable: true),
                    NotesHtml = table.Column<string>(type: "text", nullable: true),
                    IconNameSnapshot = table.Column<string>(type: "text", nullable: true),
                    MarkerColorSnapshot = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaceVisitEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaceVisitEvents_Places_PlaceId",
                        column: x => x.PlaceId,
                        principalTable: "Places",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaceVisitCandidate_LastHitUtc",
                table: "PlaceVisitCandidates",
                column: "LastHitUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PlaceVisitCandidate_UserId_PlaceId",
                table: "PlaceVisitCandidates",
                columns: new[] { "UserId", "PlaceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlaceVisitCandidates_PlaceId",
                table: "PlaceVisitCandidates",
                column: "PlaceId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaceVisitEvent_ArrivedAtUtc",
                table: "PlaceVisitEvents",
                column: "ArrivedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PlaceVisitEvent_PlaceId",
                table: "PlaceVisitEvents",
                column: "PlaceId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaceVisitEvent_UserId_EndedAtUtc",
                table: "PlaceVisitEvents",
                columns: new[] { "UserId", "EndedAtUtc" });

            // Add spatial index on Places.Location for efficient ST_DWithin queries
            // This enables fast nearest-place lookups during visit detection
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_Places_Location_Spatial""
                ON ""Places"" USING GIST (""Location"");
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop spatial index first
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Places_Location_Spatial"";");

            migrationBuilder.DropTable(
                name: "PlaceVisitCandidates");

            migrationBuilder.DropTable(
                name: "PlaceVisitEvents");

            migrationBuilder.DropColumn(
                name: "VisitedAccuracyMultiplier",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "VisitedAccuracyRejectMeters",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "VisitedMaxRadiusMeters",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "VisitedMaxSearchRadiusMeters",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "VisitedMinRadiusMeters",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "VisitedPlaceNotesSnapshotMaxHtmlChars",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "VisitedRequiredHits",
                table: "ApplicationSettings");
        }
    }
}
