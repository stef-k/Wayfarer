using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class RoutingProviderMinimumInterval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MinimumIntervalMilliseconds",
                table: "RoutingProviderConfigurations",
                type: "integer",
                nullable: false,
                defaultValue: 1000);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RoutingProviderConfigurations_MinimumIntervalMilliseconds",
                table: "RoutingProviderConfigurations",
                sql: "\"MinimumIntervalMilliseconds\" >= 0 AND \"MinimumIntervalMilliseconds\" <= 60000");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RoutingProviderConfigurations_MinimumIntervalMilliseconds",
                table: "RoutingProviderConfigurations");

            migrationBuilder.DropColumn(
                name: "MinimumIntervalMilliseconds",
                table: "RoutingProviderConfigurations");
        }
    }
}
