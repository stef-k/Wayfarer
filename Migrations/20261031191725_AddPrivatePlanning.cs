using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddPrivatePlanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Planning",
                table: "Trips",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Planning",
                table: "Segments",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Planning",
                table: "Regions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Planning",
                table: "Places",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Planning",
                table: "Areas",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Planning",
                table: "Trips");

            migrationBuilder.DropColumn(
                name: "Planning",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "Planning",
                table: "Regions");

            migrationBuilder.DropColumn(
                name: "Planning",
                table: "Places");

            migrationBuilder.DropColumn(
                name: "Planning",
                table: "Areas");
        }
    }
}
