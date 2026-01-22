using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddVisitUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Create partial unique index to prevent duplicate visits for the same user, place, and date.
            // Uses DATE() function on ArrivedAtUtc to group by calendar date.
            // Partial index (WHERE PlaceId IS NOT NULL) excludes visits to deleted places.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_PlaceVisitEvents_UserId_PlaceId_VisitDate"
                ON "PlaceVisitEvents" ("UserId", "PlaceId", (DATE("ArrivedAtUtc")))
                WHERE "PlaceId" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_PlaceVisitEvents_UserId_PlaceId_VisitDate";
                """);
        }
    }
}
