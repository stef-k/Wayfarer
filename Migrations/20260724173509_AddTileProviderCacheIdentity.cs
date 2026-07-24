using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddTileProviderCacheIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Schema-only migration: legacy rows remain null and are adopted lazily only for
            // canonical OSM, or retired in bounded normal-maintenance batches for other providers.
            migrationBuilder.DropIndex(
                name: "IX_TileCacheMetadata_Zoom_X_Y",
                table: "TileCacheMetadata");

            migrationBuilder.AddColumn<string>(
                name: "ProviderIdentity",
                table: "TileCacheMetadata",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TileCacheMetadata_ProviderIdentity_Zoom_X_Y",
                table: "TileCacheMetadata",
                columns: new[] { "ProviderIdentity", "Zoom", "X", "Y" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TileCacheMetadata_Zoom_X_Y",
                table: "TileCacheMetadata",
                columns: new[] { "Zoom", "X", "Y" },
                unique: true,
                filter: "\"ProviderIdentity\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TileCacheMetadata_ProviderIdentity_Zoom_X_Y",
                table: "TileCacheMetadata");

            migrationBuilder.DropIndex(
                name: "IX_TileCacheMetadata_Zoom_X_Y",
                table: "TileCacheMetadata");

            migrationBuilder.DropColumn(
                name: "ProviderIdentity",
                table: "TileCacheMetadata");

            migrationBuilder.CreateIndex(
                name: "IX_TileCacheMetadata_Zoom_X_Y",
                table: "TileCacheMetadata",
                columns: new[] { "Zoom", "X", "Y" },
                unique: true);
        }
    }
}
