using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddTileOutboundBudgetPerIpSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TileOutboundBudgetPerIpPerMinute",
                table: "ApplicationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 30);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TileOutboundBudgetPerIpPerMinute",
                table: "ApplicationSettings");
        }
    }
}
