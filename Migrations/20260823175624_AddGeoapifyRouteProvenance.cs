using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddGeoapifyRouteProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RouteAttribution",
                table: "Segments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RouteGeneratedAt",
                table: "Segments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RouteInstructionsJson",
                table: "Segments",
                type: "character varying(65535)",
                maxLength: 65535,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RouteMappingMode",
                table: "Segments",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RouteProvider",
                table: "Segments",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RouteProviderConfigurationId",
                table: "Segments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RouteProviderConfigurationVersion",
                table: "Segments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RouteStorageMode",
                table: "Segments",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RouteTransportProfileId",
                table: "Segments",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RouteAttribution",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "RouteGeneratedAt",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "RouteInstructionsJson",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "RouteMappingMode",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "RouteProvider",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "RouteProviderConfigurationId",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "RouteProviderConfigurationVersion",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "RouteStorageMode",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "RouteTransportProfileId",
                table: "Segments");
        }
    }
}
