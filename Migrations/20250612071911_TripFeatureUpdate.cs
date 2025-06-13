using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class TripFeatureUpdate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "NotesHtml",
                table: "Regions",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<Point>(
                name: "Center",
                table: "Regions",
                type: "geography(Point,4326)",
                nullable: true,
                oldClrType: typeof(Point),
                oldType: "geography(Point,4326)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "NotesHtml",
                table: "Regions",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<Point>(
                name: "Center",
                table: "Regions",
                type: "geography(Point,4326)",
                nullable: false,
                oldClrType: typeof(Point),
                oldType: "geography(Point,4326)",
                oldNullable: true);
        }
    }
}
