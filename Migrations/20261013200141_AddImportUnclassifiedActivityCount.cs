using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddImportUnclassifiedActivityCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UnclassifiedActivityCount",
                table: "LocationImports",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_LocationImport_UnclassifiedActivity",
                table: "LocationImports",
                sql: "\"UnclassifiedActivityCount\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_LocationImport_UnclassifiedActivity",
                table: "LocationImports");

            migrationBuilder.DropColumn(
                name: "UnclassifiedActivityCount",
                table: "LocationImports");
        }
    }
}
