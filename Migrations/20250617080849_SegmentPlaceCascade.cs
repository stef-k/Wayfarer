using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class SegmentPlaceCascade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Segments_Places_FromPlaceId",
                table: "Segments");

            migrationBuilder.DropForeignKey(
                name: "FK_Segments_Places_ToPlaceId",
                table: "Segments");

            migrationBuilder.AddForeignKey(
                name: "FK_Segments_Places_FromPlaceId",
                table: "Segments",
                column: "FromPlaceId",
                principalTable: "Places",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Segments_Places_ToPlaceId",
                table: "Segments",
                column: "ToPlaceId",
                principalTable: "Places",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Segments_Places_FromPlaceId",
                table: "Segments");

            migrationBuilder.DropForeignKey(
                name: "FK_Segments_Places_ToPlaceId",
                table: "Segments");

            migrationBuilder.AddForeignKey(
                name: "FK_Segments_Places_FromPlaceId",
                table: "Segments",
                column: "FromPlaceId",
                principalTable: "Places",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Segments_Places_ToPlaceId",
                table: "Segments",
                column: "ToPlaceId",
                principalTable: "Places",
                principalColumn: "Id");
        }
    }
}
