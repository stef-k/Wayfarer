using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddSegmentMeasurementProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EstimatedDurationSource",
                table: "Segments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                """
                UPDATE public."Segments"
                SET "EstimatedDurationSource" = 1
                WHERE "EstimatedDuration" IS NOT NULL;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Segments_EstimatedDurationSource",
                table: "Segments",
                sql: "\"EstimatedDurationSource\" IN (0, 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Removing this column intentionally loses Automatic/Manual provenance for values written after Up.
            migrationBuilder.DropCheckConstraint(
                name: "CK_Segments_EstimatedDurationSource",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "EstimatedDurationSource",
                table: "Segments");
        }
    }
}
