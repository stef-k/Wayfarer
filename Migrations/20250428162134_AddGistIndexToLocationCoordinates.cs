using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddGistIndexToLocationCoordinates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Location_Coordinates",
                table: "Locations");

            migrationBuilder.CreateIndex(
                name: "IX_Location_Coordinates",
                table: "Locations",
                column: "Coordinates")
                .Annotation("Npgsql:IndexMethod", "GIST");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Location_Coordinates",
                table: "Locations");

            migrationBuilder.CreateIndex(
                name: "IX_Location_Coordinates",
                table: "Locations",
                column: "Coordinates");
        }
    }
}
