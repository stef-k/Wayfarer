using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class LocationHistoryPreparation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:postgis", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.AddColumn<DateTime>(
                name: "CaptureLocalTimestamp",
                table: "Locations",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CaptureOffsetSeconds",
                table: "Locations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CaptureTimeZoneId",
                table: "Locations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HistorySearchText",
                table: "Locations",
                type: "text",
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<string>(
                name: "HistoryTextVersion",
                table: "Locations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HistoryTimeFailureCode",
                table: "Locations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HistoryTimeStatus",
                table: "Locations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HistoryTimeVersion",
                table: "Locations",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HistoryPreparationStates",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "text", nullable: false),
                    TargetVersion = table.Column<string>(type: "text", nullable: false),
                    ActiveVersion = table.Column<string>(type: "text", nullable: true),
                    Ready = table.Column<bool>(type: "boolean", nullable: false),
                    WriterVersion = table.Column<string>(type: "text", nullable: true),
                    Scanning = table.Column<bool>(type: "boolean", nullable: false),
                    LastId = table.Column<int>(type: "integer", nullable: false),
                    Examined = table.Column<long>(type: "bigint", nullable: false),
                    Prepared = table.Column<long>(type: "bigint", nullable: false),
                    LastFailure = table.Column<string>(type: "text", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoryPreparationStates", x => x.Kind);
                    table.CheckConstraint("CK_HistoryPreparation_Kind", "\"Kind\" IN ('time', 'text')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Location_HistoryCapture",
                table: "Locations",
                columns: new[] { "UserId", "CaptureLocalTimestamp", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Location_HistoryText",
                table: "Locations",
                column: "HistorySearchText")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Location_HistoryUtc",
                table: "Locations",
                columns: new[] { "UserId", "LocalTimestamp", "Id" })
                .Annotation("Npgsql:IndexInclude", new[] { "Coordinates", "CaptureLocalTimestamp", "ActivityTypeId" });

            // The geography index cannot serve geometry-expression rectangle predicates.
            migrationBuilder.Sql("CREATE INDEX \"IX_Location_HistoryGeometry\" ON \"Locations\" USING gist ((\"Coordinates\"::geometry))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Location_HistoryText",
                table: "Locations",
                sql: "(\"HistorySearchText\" IS NULL) = (\"HistoryTextVersion\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Location_HistoryTime",
                table: "Locations",
                sql: "(\"HistoryTimeVersion\" IS NULL AND \"HistoryTimeStatus\" IS NULL AND \"HistoryTimeFailureCode\" IS NULL\n    AND \"CaptureLocalTimestamp\" IS NULL AND \"CaptureTimeZoneId\" IS NULL AND \"CaptureOffsetSeconds\" IS NULL)\nOR (\"HistoryTimeVersion\" IS NOT NULL AND \"HistoryTimeStatus\" IS NOT NULL AND (\n    (\"HistoryTimeStatus\" = 'resolved' AND \"HistoryTimeFailureCode\" IS NULL AND \"CaptureLocalTimestamp\" IS NOT NULL\n        AND \"CaptureTimeZoneId\" IS NOT NULL AND \"CaptureOffsetSeconds\" IS NOT NULL)\n    OR (\"HistoryTimeStatus\" = 'unresolved' AND \"HistoryTimeFailureCode\" IS NOT NULL\n        AND \"CaptureLocalTimestamp\" IS NULL AND \"CaptureTimeZoneId\" IS NULL AND \"CaptureOffsetSeconds\" IS NULL)))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX \"IX_Location_HistoryGeometry\"");

            migrationBuilder.DropTable(
                name: "HistoryPreparationStates");

            migrationBuilder.DropIndex(
                name: "IX_Location_HistoryCapture",
                table: "Locations");

            migrationBuilder.DropIndex(
                name: "IX_Location_HistoryText",
                table: "Locations");

            migrationBuilder.DropIndex(
                name: "IX_Location_HistoryUtc",
                table: "Locations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Location_HistoryText",
                table: "Locations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Location_HistoryTime",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "CaptureLocalTimestamp",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "CaptureOffsetSeconds",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "CaptureTimeZoneId",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "HistorySearchText",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "HistoryTextVersion",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "HistoryTimeFailureCode",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "HistoryTimeStatus",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "HistoryTimeVersion",
                table: "Locations");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .Annotation("Npgsql:PostgresExtension:postgis", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:postgis", ",,");
        }
    }
}
