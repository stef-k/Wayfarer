using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class ImportEnrichmentHandoffIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_LocationImport_EnrichmentHandoff",
                table: "LocationImports",
                column: "Id",
                filter: "\"EnrichmentHandoffPending\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LocationImport_EnrichmentHandoff",
                table: "LocationImports");
        }
    }
}
