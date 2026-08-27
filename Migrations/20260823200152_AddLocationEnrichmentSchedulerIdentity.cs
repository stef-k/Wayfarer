using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddLocationEnrichmentSchedulerIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SchedulerId",
                table: "LocationEnrichmentWorkflows",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_LocationEnrichmentWorkflows_SchedulerId",
                table: "LocationEnrichmentWorkflows",
                column: "SchedulerId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LocationEnrichmentWorkflows_SchedulerId",
                table: "LocationEnrichmentWorkflows");

            migrationBuilder.DropColumn(
                name: "SchedulerId",
                table: "LocationEnrichmentWorkflows");
        }
    }
}
