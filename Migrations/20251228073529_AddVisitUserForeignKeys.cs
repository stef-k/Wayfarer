using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddVisitUserForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_PlaceVisitCandidates_AspNetUsers_UserId",
                table: "PlaceVisitCandidates",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PlaceVisitEvents_AspNetUsers_UserId",
                table: "PlaceVisitEvents",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PlaceVisitCandidates_AspNetUsers_UserId",
                table: "PlaceVisitCandidates");

            migrationBuilder.DropForeignKey(
                name: "FK_PlaceVisitEvents_AspNetUsers_UserId",
                table: "PlaceVisitEvents");
        }
    }
}
