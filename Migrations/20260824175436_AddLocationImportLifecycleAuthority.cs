using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddLocationImportLifecycleAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletionRequestedAtUtc",
                table: "LocationImports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExecutionEpoch",
                table: "LocationImports",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "ProjectionPending",
                table: "LocationImports",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "StopRequestedAtUtc",
                table: "LocationImports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "LocationImports",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddCheckConstraint(
                name: "CK_LocationImport_ExecutionEpoch",
                table: "LocationImports",
                sql: "\"ExecutionEpoch\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_LocationImport_ExecutionEpoch",
                table: "LocationImports");

            migrationBuilder.DropColumn(
                name: "DeletionRequestedAtUtc",
                table: "LocationImports");

            migrationBuilder.DropColumn(
                name: "ExecutionEpoch",
                table: "LocationImports");

            migrationBuilder.DropColumn(
                name: "ProjectionPending",
                table: "LocationImports");

            migrationBuilder.DropColumn(
                name: "StopRequestedAtUtc",
                table: "LocationImports");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "LocationImports");
        }
    }
}
