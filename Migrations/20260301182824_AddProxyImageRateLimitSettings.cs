using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddProxyImageRateLimitSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ProxyImageRateLimitEnabled",
                table: "ApplicationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "ProxyImageRateLimitPerMinute",
                table: "ApplicationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 200);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProxyImageRateLimitEnabled",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "ProxyImageRateLimitPerMinute",
                table: "ApplicationSettings");
        }
    }
}
