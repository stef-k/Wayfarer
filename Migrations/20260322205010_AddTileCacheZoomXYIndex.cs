using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddTileCacheZoomXYIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_TileCacheMetadata_Zoom_X_Y",
                table: "TileCacheMetadata",
                columns: new[] { "Zoom", "X", "Y" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TileCacheMetadata_Zoom_X_Y",
                table: "TileCacheMetadata");
        }
    }
}
