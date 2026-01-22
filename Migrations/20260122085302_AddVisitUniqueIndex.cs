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
            // Step 1: Create an immutable function for extracting date from timestamptz.
            // PostgreSQL's DATE() function is not IMMUTABLE because it depends on session timezone.
            // This wrapper explicitly uses UTC, making the result deterministic and safe for indexing.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION utc_date(timestamptz)
                RETURNS date AS $$
                    SELECT ($1 AT TIME ZONE 'UTC')::date;
                $$ LANGUAGE sql IMMUTABLE STRICT;
                """);

            // Step 2: Remove duplicate visits, keeping the oldest (earliest ArrivedAtUtc) for each
            // (UserId, PlaceId, Date) combination. This ensures the unique index can be created.
            migrationBuilder.Sql("""
                DELETE FROM "PlaceVisitEvents"
                WHERE "Id" IN (
                    SELECT "Id" FROM (
                        SELECT "Id",
                               ROW_NUMBER() OVER (
                                   PARTITION BY "UserId", "PlaceId", utc_date("ArrivedAtUtc")
                                   ORDER BY "ArrivedAtUtc" ASC, "Id" ASC
                               ) as rn
                        FROM "PlaceVisitEvents"
                        WHERE "PlaceId" IS NOT NULL
                    ) ranked
                    WHERE rn > 1
                );
                """);

            // Step 3: Create partial unique index to prevent future duplicate visits.
            // Uses the immutable utc_date() function to group by calendar date in UTC.
            // Partial index (WHERE PlaceId IS NOT NULL) excludes visits to deleted places.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_PlaceVisitEvents_UserId_PlaceId_VisitDate"
                ON "PlaceVisitEvents" ("UserId", "PlaceId", (utc_date("ArrivedAtUtc")))
                WHERE "PlaceId" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_PlaceVisitEvents_UserId_PlaceId_VisitDate";
                """);

            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS utc_date(timestamptz);
                """);
        }
    }
}
