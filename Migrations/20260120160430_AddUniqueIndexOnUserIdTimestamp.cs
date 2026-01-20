using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueIndexOnUserIdTimestamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add unique index to prevent duplicate locations for the same user at the same timestamp.
            // This guards against race conditions in concurrent imports/API calls.
            migrationBuilder.CreateIndex(
                name: "IX_Locations_UserId_Timestamp_Unique",
                table: "Locations",
                columns: new[] { "UserId", "Timestamp" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Locations_UserId_Timestamp_Unique",
                table: "Locations");
        }
    }
}
