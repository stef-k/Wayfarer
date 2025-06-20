using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddTripMapView : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CenterLat",
                table: "Trips",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CenterLon",
                table: "Trips",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Zoom",
                table: "Trips",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CenterLat",
                table: "Trips");

            migrationBuilder.DropColumn(
                name: "CenterLon",
                table: "Trips");

            migrationBuilder.DropColumn(
                name: "Zoom",
                table: "Trips");
        }
    }
}
