using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddTileProviderPolicySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TileOutboundBudgetHistorical30Acknowledged",
                table: "ApplicationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "TileProviderAdvancedLimitsEnabled",
                table: "ApplicationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TileProviderBurstCapacity",
                table: "ApplicationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 20);

            migrationBuilder.AddColumn<int>(
                name: "TileProviderFallbackBaseDelayMs",
                table: "ApplicationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 500);

            migrationBuilder.AddColumn<int>(
                name: "TileProviderFallbackDelayCapSeconds",
                table: "ApplicationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 4);

            migrationBuilder.AddColumn<int>(
                name: "TileProviderMaxAttempts",
                table: "ApplicationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "TileProviderMaxConcurrency",
                table: "ApplicationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 6);

            migrationBuilder.AddColumn<int>(
                name: "TileProviderMaxIndividualWaitSeconds",
                table: "ApplicationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "TileProviderSustainedRequestsPerSecond",
                table: "ApplicationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 6);

            migrationBuilder.AddColumn<int>(
                name: "TileProviderTotalRetryCeilingSeconds",
                table: "ApplicationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 45);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TileOutboundBudgetHistorical30Acknowledged",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "TileProviderAdvancedLimitsEnabled",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "TileProviderBurstCapacity",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "TileProviderFallbackBaseDelayMs",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "TileProviderFallbackDelayCapSeconds",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "TileProviderMaxAttempts",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "TileProviderMaxConcurrency",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "TileProviderMaxIndividualWaitSeconds",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "TileProviderSustainedRequestsPerSecond",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "TileProviderTotalRetryCeilingSeconds",
                table: "ApplicationSettings");
        }
    }
}
