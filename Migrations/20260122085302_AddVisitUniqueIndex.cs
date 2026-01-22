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
            // Step 1: Remove duplicate visits, keeping the oldest (earliest ArrivedAtUtc) for each
            // (UserId, PlaceId, Date) combination. This ensures the unique index can be created.
            migrationBuilder.Sql("""
                DELETE FROM "PlaceVisitEvents"
                WHERE "Id" IN (
                    SELECT "Id" FROM (
                        SELECT "Id",
                               ROW_NUMBER() OVER (
                                   PARTITION BY "UserId", "PlaceId", DATE("ArrivedAtUtc")
                                   ORDER BY "ArrivedAtUtc" ASC, "Id" ASC
                               ) as rn
                        FROM "PlaceVisitEvents"
                        WHERE "PlaceId" IS NOT NULL
                    ) ranked
                    WHERE rn > 1
                );
                """);

            // Step 2: Create partial unique index to prevent future duplicate visits.
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
