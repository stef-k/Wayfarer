using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddVisitSourceAndSuggestionSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "PlaceVisitEvents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VisitedSuggestionMaxRadiusMultiplier",
                table: "ApplicationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 50);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Source",
                table: "PlaceVisitEvents");

            migrationBuilder.DropColumn(
                name: "VisitedSuggestionMaxRadiusMultiplier",
                table: "ApplicationSettings");
        }
    }
}
