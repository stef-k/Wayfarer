using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddTileTrafficMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TileTrafficMode",
                table: "ApplicationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // The single settings row migrates deterministically without consulting dormant numeric values.
            migrationBuilder.Sql("""
                UPDATE "ApplicationSettings"
                SET "TileTrafficMode" = 2
                WHERE lower("TileProviderKey") = 'custom'
                  AND "TileProviderAdvancedLimitsEnabled" = TRUE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TileTrafficMode",
                table: "ApplicationSettings");
        }
    }
}
