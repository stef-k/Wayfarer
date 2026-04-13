using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddTileMetadataHotCacheSizeSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TileMetadataHotCacheSizeMB",
                table: "ApplicationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 64);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TileMetadataHotCacheSizeMB",
                table: "ApplicationSettings");
        }
    }
}
