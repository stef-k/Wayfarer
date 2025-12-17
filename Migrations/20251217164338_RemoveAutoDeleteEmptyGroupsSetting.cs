using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class RemoveAutoDeleteEmptyGroupsSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoDeleteEmptyGroups",
                table: "ApplicationSettings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoDeleteEmptyGroups",
                table: "ApplicationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }
    }
}
