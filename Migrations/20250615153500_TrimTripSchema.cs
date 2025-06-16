using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class TrimTripSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rename note fields to the new unified "Notes" name
            migrationBuilder.RenameColumn(
                name: "NotesHtml",
                table: "Trips",
                newName: "Notes");

            migrationBuilder.RenameColumn(
                name: "NotesHtml",
                table: "Segments",
                newName: "Notes");

            migrationBuilder.RenameColumn(
                name: "NotesHtml",
                table: "Regions",
                newName: "Notes");

            migrationBuilder.RenameColumn(
                name: "DescriptionHtml",
                table: "Places",
                newName: "Notes");

            // Remove unused or deprecated fields
            migrationBuilder.DropColumn(name: "Days", table: "Trips");
            migrationBuilder.DropColumn(name: "EndDate", table: "Trips");
            migrationBuilder.DropColumn(name: "StartDate", table: "Trips");

            migrationBuilder.DropColumn(name: "Boundary", table: "Regions");
            migrationBuilder.DropColumn(name: "Days", table: "Regions");
            migrationBuilder.DropColumn(name: "IsVisible", table: "Regions");

            migrationBuilder.DropColumn(name: "IsVisible", table: "Places");
            migrationBuilder.DropColumn(name: "OpeningHoursJson", table: "Places");
            migrationBuilder.DropColumn(name: "PhoneNumber", table: "Places");
            migrationBuilder.DropColumn(name: "PriceCategory", table: "Places");
            migrationBuilder.DropColumn(name: "RouteTrace", table: "Places");
            migrationBuilder.DropColumn(name: "SuggestedDuration", table: "Places");
            migrationBuilder.DropColumn(name: "WebsiteUrl", table: "Places");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore removed fields
            migrationBuilder.AddColumn<int>(
                name: "Days",
                table: "Trips",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EndDate",
                table: "Trips",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartDate",
                table: "Trips",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Polygon>(
                name: "Boundary",
                table: "Regions",
                type: "geography(Polygon,4326)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Days",
                table: "Regions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsVisible",
                table: "Regions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsVisible",
                table: "Places",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OpeningHoursJson",
                table: "Places",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhoneNumber",
                table: "Places",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PriceCategory",
                table: "Places",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<LineString>(
                name: "RouteTrace",
                table: "Places",
                type: "geography(LineString,4326)",
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "SuggestedDuration",
                table: "Places",
                type: "interval",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebsiteUrl",
                table: "Places",
                type: "text",
                nullable: true);

            // Rename note fields back to legacy names
            migrationBuilder.RenameColumn(
                name: "Notes",
                table: "Trips",
                newName: "NotesHtml");

            migrationBuilder.RenameColumn(
                name: "Notes",
                table: "Segments",
                newName: "NotesHtml");

            migrationBuilder.RenameColumn(
                name: "Notes",
                table: "Regions",
                newName: "NotesHtml");

            migrationBuilder.RenameColumn(
                name: "Notes",
                table: "Places",
                newName: "DescriptionHtml");
        }
    }
}
