using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class ConstrainLocationImportLifecycleState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_LocationImport_LifecycleState",
                table: "LocationImports",
                sql: "((\"Status\" = 'In Progress' AND \"StopRequestedAtUtc\" IS NULL AND \"DeletionRequestedAtUtc\" IS NULL) OR (\"Status\" = 'Stopping' AND \"StopRequestedAtUtc\" IS NOT NULL AND \"DeletionRequestedAtUtc\" IS NULL AND \"ProjectionPending\") OR (\"Status\" = 'Stopped' AND NOT \"ProjectionPending\") OR (\"Status\" IN ('Completed','Failed') AND \"StopRequestedAtUtc\" IS NULL AND NOT \"ProjectionPending\")) IS TRUE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_LocationImport_LifecycleState",
                table: "LocationImports");
        }
    }
}
