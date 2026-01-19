using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddTileProviderSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TileProviderApiKey",
                table: "ApplicationSettings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TileProviderAttribution",
                table: "ApplicationSettings",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TileProviderKey",
                table: "ApplicationSettings",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TileProviderUrlTemplate",
                table: "ApplicationSettings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TileProviderApiKey",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "TileProviderAttribution",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "TileProviderKey",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "TileProviderUrlTemplate",
                table: "ApplicationSettings");
        }
    }
}
