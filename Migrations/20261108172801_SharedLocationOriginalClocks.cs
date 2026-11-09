using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class SharedLocationOriginalClocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OriginalLocalTimestamp",
                table: "Locations",
                type: "character varying(29)",
                maxLength: 29,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalTimeZoneId",
                table: "Locations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OriginalUtcOffsetSeconds",
                table: "Locations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SubmittedAtUtc",
                table: "Locations",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OriginalLocalTimestamp",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "OriginalTimeZoneId",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "OriginalUtcOffsetSeconds",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "SubmittedAtUtc",
                table: "Locations");
        }
    }
}
