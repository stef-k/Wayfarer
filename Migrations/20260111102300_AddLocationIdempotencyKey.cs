using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddLocationIdempotencyKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Locations_UserId",
                table: "Locations");

            migrationBuilder.AddColumn<Guid>(
                name: "IdempotencyKey",
                table: "Locations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Location_UserId_IdempotencyKey",
                table: "Locations",
                columns: new[] { "UserId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Location_UserId_IdempotencyKey",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "Locations");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_UserId",
                table: "Locations",
                column: "UserId");
        }
    }
}
