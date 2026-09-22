using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class RepairHiddenAreaSrid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // EF executes the preflight and metadata repair in its normal migration transaction.
            // Keep writers out between the check and update; drawing vertices are already lon/lat.
            migrationBuilder.Sql("""
                LOCK TABLE "HiddenAreas" IN SHARE ROW EXCLUSIVE MODE;
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "HiddenAreas"
                               WHERE ST_SRID("Area") NOT IN (0, 4326)) THEN
                        RAISE EXCEPTION 'HiddenAreas contains unexpected SRIDs; repair aborted.'
                            USING HINT = 'Inspect ST_SRID("Area") in "HiddenAreas" and verify the source coordinate system. Correct unexpected nonzero SRIDs before retrying this migration; do not blindly relabel them.';
                    END IF;
                END $$;

                UPDATE "HiddenAreas"
                SET "Area" = ST_SetSRID("Area", 4326)
                WHERE ST_SRID("Area") = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately retain corrected metadata: repaired polygons cannot be distinguished
            // from originally correct or subsequently created 4326 polygons. Resetting all to 0
            // would corrupt valid metadata and reintroduce mixed-SRID public query failures.
        }
    }
}
