using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddTileCacheConditionalRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ETag",
                table: "TileCacheMetadata",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAtUtc",
                table: "TileCacheMetadata",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastModifiedUpstream",
                table: "TileCacheMetadata",
                type: "timestamp with time zone",
                nullable: true);

            // Migrate existing deployments from non-canonical subdomain URL to canonical URL.
            // OSM states non-canonical subdomains "may be slower or withdrawn without notice."
            migrationBuilder.Sql(
                "UPDATE \"ApplicationSettings\" " +
                "SET \"TileProviderUrlTemplate\" = 'https://tile.openstreetmap.org/{z}/{x}/{y}.png' " +
                "WHERE \"TileProviderUrlTemplate\" = 'https://a.tile.openstreetmap.org/{z}/{x}/{y}.png'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ETag",
                table: "TileCacheMetadata");

            migrationBuilder.DropColumn(
                name: "ExpiresAtUtc",
                table: "TileCacheMetadata");

            migrationBuilder.DropColumn(
                name: "LastModifiedUpstream",
                table: "TileCacheMetadata");

            // Revert canonical URL back to non-canonical subdomain URL.
            migrationBuilder.Sql(
                "UPDATE \"ApplicationSettings\" " +
                "SET \"TileProviderUrlTemplate\" = 'https://a.tile.openstreetmap.org/{z}/{x}/{y}.png' " +
                "WHERE \"TileProviderUrlTemplate\" = 'https://tile.openstreetmap.org/{z}/{x}/{y}.png'");
        }
    }
}
