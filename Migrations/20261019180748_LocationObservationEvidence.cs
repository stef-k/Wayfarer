using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class LocationObservationEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CoordinateOrigin",
                table: "Locations",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ObservedAgeAtEventMs",
                table: "Locations",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ObservedAtUtc",
                table: "Locations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PresenceObservedAtUtc",
                table: "Locations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Location_UserId_PresenceObservedAtUtc_Id",
                table: "Locations",
                columns: new[] { "UserId", "PresenceObservedAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Location_UserId_PresenceObservedAtUtc_Id",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "CoordinateOrigin",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "ObservedAgeAtEventMs",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "ObservedAtUtc",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "PresenceObservedAtUtc",
                table: "Locations");
        }
    }
}
