using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddSegmentWaypoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SegmentWaypoints",
                columns: table => new
                {
                    SegmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    RouteVertexIndex = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SegmentWaypoints", x => new { x.SegmentId, x.PlaceId });
                    table.CheckConstraint("CK_SegmentWaypoint_Position", "\"Position\" >= 0");
                    table.CheckConstraint("CK_SegmentWaypoint_RouteVertexIndex", "\"RouteVertexIndex\" IS NULL OR \"RouteVertexIndex\" > 0");
                    table.ForeignKey(
                        name: "FK_SegmentWaypoints_Places_PlaceId",
                        column: x => x.PlaceId,
                        principalTable: "Places",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SegmentWaypoints_Segments_SegmentId",
                        column: x => x.SegmentId,
                        principalTable: "Segments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SegmentWaypoints_PlaceId",
                table: "SegmentWaypoints",
                column: "PlaceId");

            migrationBuilder.CreateIndex(
                name: "IX_SegmentWaypoints_SegmentId_Position",
                table: "SegmentWaypoints",
                columns: new[] { "SegmentId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SegmentWaypoints_SegmentId_RouteVertexIndex",
                table: "SegmentWaypoints",
                columns: new[] { "SegmentId", "RouteVertexIndex" },
                unique: true,
                filter: "\"RouteVertexIndex\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SegmentWaypoints");
        }
    }
}
