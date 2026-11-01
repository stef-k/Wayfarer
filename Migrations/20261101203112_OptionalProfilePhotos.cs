using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class OptionalProfilePhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProfilePhotoVersion",
                table: "AspNetUsers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowProfilePhotoOnPublicTrips",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_ProfilePhotoVersion",
                table: "AspNetUsers",
                column: "ProfilePhotoVersion");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_ProfilePhotoVersion",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ProfilePhotoVersion",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ShowProfilePhotoOnPublicTrips",
                table: "AspNetUsers");
        }
    }
}
