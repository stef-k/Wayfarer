using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class LocationDeletionMarkers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SkippedDeleted",
                table: "LocationImports",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "LocationDeletionMarkers",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocationDeletionMarkers", x => new { x.UserId, x.IdempotencyKey });
                    table.ForeignKey(
                        name: "FK_LocationDeletionMarkers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_LocationImport_SkippedDeleted",
                table: "LocationImports",
                sql: "\"SkippedDeleted\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LocationDeletionMarkers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LocationImport_SkippedDeleted",
                table: "LocationImports");

            migrationBuilder.DropColumn(
                name: "SkippedDeleted",
                table: "LocationImports");
        }
    }
}
