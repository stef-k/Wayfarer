using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddLocationMetadataAndDeduplication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppBuild",
                table: "Locations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppVersion",
                table: "Locations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BatteryLevel",
                table: "Locations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Bearing",
                table: "Locations",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeviceModel",
                table: "Locations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCharging",
                table: "Locations",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsUserInvoked",
                table: "Locations",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OsVersion",
                table: "Locations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "Locations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Locations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SkippedDuplicates",
                table: "LocationImports",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppBuild",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "AppVersion",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "BatteryLevel",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "Bearing",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "DeviceModel",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "IsCharging",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "IsUserInvoked",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "OsVersion",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "SkippedDuplicates",
                table: "LocationImports");
        }
    }
}
