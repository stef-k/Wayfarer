using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddLocationEnrichmentAttemptSelectionGeneration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LocationEnrichmentAttempts_Locations_LocationId",
                table: "LocationEnrichmentAttempts");

            migrationBuilder.DropIndex(
                name: "IX_LocationEnrichmentAttempts_LocationId",
                table: "LocationEnrichmentAttempts");

            migrationBuilder.AddColumn<int>(
                name: "SelectionGeneration",
                table: "LocationEnrichmentAttempts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Locations_UserId_Id",
                table: "Locations",
                columns: new[] { "UserId", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_LocationEnrichmentAttempts_Locations_UserId_LocationId",
                table: "LocationEnrichmentAttempts",
                columns: new[] { "UserId", "LocationId" },
                principalTable: "Locations",
                principalColumns: new[] { "UserId", "Id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LocationEnrichmentAttempts_Locations_UserId_LocationId",
                table: "LocationEnrichmentAttempts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Locations_UserId_Id",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "SelectionGeneration",
                table: "LocationEnrichmentAttempts");

            migrationBuilder.CreateIndex(
                name: "IX_LocationEnrichmentAttempts_LocationId",
                table: "LocationEnrichmentAttempts",
                column: "LocationId");

            migrationBuilder.AddForeignKey(
                name: "FK_LocationEnrichmentAttempts_Locations_LocationId",
                table: "LocationEnrichmentAttempts",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
