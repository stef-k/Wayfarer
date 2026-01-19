using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddTileRateLimitSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TileRateLimitEnabled",
                table: "ApplicationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "TileRateLimitPerMinute",
                table: "ApplicationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 500);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TileRateLimitEnabled",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "TileRateLimitPerMinute",
                table: "ApplicationSettings");
        }
    }
}
