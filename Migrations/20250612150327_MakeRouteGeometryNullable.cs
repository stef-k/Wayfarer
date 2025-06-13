using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class MakeRouteGeometryNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<LineString>(
                name: "RouteGeometry",
                table: "Segments",
                type: "geography(LineString,4326)",
                nullable: true,
                oldClrType: typeof(LineString),
                oldType: "geography(LineString,4326)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<LineString>(
                name: "RouteGeometry",
                table: "Segments",
                type: "geography(LineString,4326)",
                nullable: false,
                oldClrType: typeof(LineString),
                oldType: "geography(LineString,4326)",
                oldNullable: true);
        }
    }
}
