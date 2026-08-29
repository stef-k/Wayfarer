using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddResolvedFeatureMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResolvedFeatureName",
                table: "Places",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedFeatureType",
                table: "Places",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedFeatureName",
                table: "Locations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedFeatureType",
                table: "Locations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResolvedFeatureName",
                table: "Places");

            migrationBuilder.DropColumn(
                name: "ResolvedFeatureType",
                table: "Places");

            migrationBuilder.DropColumn(
                name: "ResolvedFeatureName",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "ResolvedFeatureType",
                table: "Locations");
        }
    }
}
