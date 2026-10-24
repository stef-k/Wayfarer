using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <summary>Adds capture membership status to the existing UTC covering payload during quiesced maintenance.</summary>
    public partial class LocationHistoryUtcStatusCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Ordinary transactional replacement: DROP blocks table access until commit; failed CREATE restores the old index.
            migrationBuilder.DropIndex(
                name: "IX_Location_HistoryUtc",
                table: "Locations");

            migrationBuilder.CreateIndex(
                name: "IX_Location_HistoryUtc",
                table: "Locations",
                columns: new[] { "UserId", "LocalTimestamp", "Id" })
                .Annotation("Npgsql:IndexInclude", new[] { "Coordinates", "CaptureLocalTimestamp", "ActivityTypeId", "HistoryTimeStatus" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rollback rebuilds the original payload under the same maintenance/transaction requirements.
            migrationBuilder.DropIndex(
                name: "IX_Location_HistoryUtc",
                table: "Locations");

            migrationBuilder.CreateIndex(
                name: "IX_Location_HistoryUtc",
                table: "Locations",
                columns: new[] { "UserId", "LocalTimestamp", "Id" })
                .Annotation("Npgsql:IndexInclude", new[] { "Coordinates", "CaptureLocalTimestamp", "ActivityTypeId" });
        }
    }
}
