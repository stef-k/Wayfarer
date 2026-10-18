using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <summary>Stops unchanged on historical conflicts and enforces exact fallback uniqueness per Trip.</summary>
    public partial class CanonicalUnassignedRegion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fence writers before the diagnostic, retaining normal transactional migration semantics.
            migrationBuilder.Sql("""
                LOCK TABLE public."Regions" IN SHARE ROW EXCLUSIVE MODE;
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM public."Regions"
                        WHERE "Name" = 'Unassigned Places'
                        GROUP BY "TripId" HAVING COUNT(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'Cannot enforce canonical Region uniqueness: duplicate exact Unassigned Places Regions exist.'
                            USING ERRCODE = '23505', CONSTRAINT = 'UX_Regions_Trip_UnassignedPlaces',
                            HINT = 'Upgrade aborted unchanged. Keep writers paused, retain a verified backup, run the documented private diagnostics and obtain maintainer-approved reconciliation before retrying.';
                    END IF;
                END $$;
                """);
            migrationBuilder.CreateIndex(
                name: "UX_Regions_Trip_UnassignedPlaces",
                table: "Regions",
                column: "TripId",
                unique: true,
                filter: "\"Name\" = 'Unassigned Places'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Regions_Trip_UnassignedPlaces",
                table: "Regions");
        }
    }
}
