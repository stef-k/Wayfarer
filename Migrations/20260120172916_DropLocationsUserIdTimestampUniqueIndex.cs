using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class DropLocationsUserIdTimestampUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop the unique index if it exists to unblock migrations on legacy data.
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Locations_UserId_Timestamp_Unique\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Recreate the unique index; rollback may fail if duplicate data still exists.
            migrationBuilder.CreateIndex(
                name: "IX_Locations_UserId_Timestamp_Unique",
                table: "Locations",
                columns: new[] { "UserId", "Timestamp" },
                unique: true);
        }
    }
}
