using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <summary>
    /// Adds indexes to Country, Region, and Place columns in Locations table
    /// to optimize statistics queries that group by these fields
    /// </summary>
    public partial class AddLocationGeographyIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Locations_Country",
                table: "Locations",
                column: "Country");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_Region",
                table: "Locations",
                column: "Region");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_Place",
                table: "Locations",
                column: "Place");

            // Composite index for hierarchical queries (Country -> Region -> Place)
            migrationBuilder.CreateIndex(
                name: "IX_Locations_Country_Region_Place",
                table: "Locations",
                columns: new[] { "Country", "Region", "Place" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Locations_Country",
                table: "Locations");

            migrationBuilder.DropIndex(
                name: "IX_Locations_Region",
                table: "Locations");

            migrationBuilder.DropIndex(
                name: "IX_Locations_Place",
                table: "Locations");

            migrationBuilder.DropIndex(
                name: "IX_Locations_Country_Region_Place",
                table: "Locations");
        }
    }
}
