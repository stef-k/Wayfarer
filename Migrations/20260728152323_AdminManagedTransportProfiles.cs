using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AdminManagedTransportProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Mode",
                table: "Segments",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<Guid>(
                name: "TransportProfileId",
                table: "Segments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TransportProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Category = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    PlanningSpeedKmh = table.Column<double>(type: "double precision", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsSeeded = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransportProfiles", x => x.Id);
                    table.CheckConstraint("CK_TransportProfile_NormalizedKey", "\"Key\" = lower(btrim(\"Key\")) AND length(\"Key\") > 0");
                    table.CheckConstraint("CK_TransportProfile_PlanningSpeedKmh", "\"PlanningSpeedKmh\" IS NULL OR (\"PlanningSpeedKmh\" > 0 AND isfinite(\"PlanningSpeedKmh\"))");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Segments_TransportProfileId",
                table: "Segments",
                column: "TransportProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_TransportProfiles_Key",
                table: "TransportProfiles",
                column: "Key",
                unique: true);

            migrationBuilder.Sql(
                """
                INSERT INTO "TransportProfiles"
                    ("Id", "Key", "Label", "Category", "PlanningSpeedKmh", "SortOrder", "IsActive", "Description", "IsSeeded")
                VALUES
                    ('11111111-0000-0000-0000-000000000001', 'walk', 'Walk', 'Active', 5, 10, TRUE, 'Average planning assumption; use a manual duration when the service differs.', TRUE),
                    ('11111111-0000-0000-0000-000000000002', 'bicycle', 'Bicycle', 'Active', 15, 20, TRUE, 'Average planning assumption; use a manual duration when the service differs.', TRUE),
                    ('11111111-0000-0000-0000-000000000003', 'bike', 'Motorcycle', 'Road', 40, 30, TRUE, 'Average planning assumption; use a manual duration when the service differs.', TRUE),
                    ('11111111-0000-0000-0000-000000000004', 'car', 'Car', 'Road', 60, 40, TRUE, 'Average planning assumption; use a manual duration when the service differs.', TRUE),
                    ('11111111-0000-0000-0000-000000000005', 'bus', 'Bus / coach', 'Road', 35, 50, TRUE, 'Average planning assumption; use a manual duration when the service differs.', TRUE),
                    ('11111111-0000-0000-0000-000000000006', 'tram', 'Tram / streetcar', 'Urban rail', 20, 60, TRUE, 'Average planning assumption; use a manual duration when the service differs.', TRUE),
                    ('11111111-0000-0000-0000-000000000007', 'metro', 'Metro / subway', 'Urban rail', 35, 70, TRUE, 'Average planning assumption; use a manual duration when the service differs.', TRUE),
                    ('11111111-0000-0000-0000-000000000008', 'regional-train', 'Regional train', 'Rail', 70, 80, TRUE, 'Average planning assumption; use a manual duration when the service differs.', TRUE),
                    ('11111111-0000-0000-0000-000000000009', 'train', 'Train (general)', 'Rail', 100, 90, TRUE, 'Average planning assumption; use a manual duration when the service differs.', TRUE),
                    ('11111111-0000-0000-0000-000000000010', 'intercity-train', 'Intercity train', 'Rail', 120, 100, TRUE, 'Average planning assumption; use a manual duration when the service differs.', TRUE),
                    ('11111111-0000-0000-0000-000000000011', 'high-speed-train', 'High-speed train', 'Rail', 250, 110, TRUE, 'Average planning assumption; use a manual duration when the service differs.', TRUE),
                    ('11111111-0000-0000-0000-000000000012', 'ferry', 'Ferry', 'Water', 30, 120, TRUE, 'Average planning assumption; use a manual duration when the service differs.', TRUE),
                    ('11111111-0000-0000-0000-000000000013', 'boat', 'Boat', 'Water', 25, 130, TRUE, 'Average planning assumption; use a manual duration when the service differs.', TRUE),
                    ('11111111-0000-0000-0000-000000000014', 'flight', 'Flight', 'Air', 800, 140, TRUE, 'Average planning assumption; use a manual duration when the service differs.', TRUE),
                    ('11111111-0000-0000-0000-000000000015', 'helicopter', 'Helicopter', 'Air', 200, 150, TRUE, 'Average planning assumption; use a manual duration when the service differs.', TRUE);

                WITH legacy AS (
                    SELECT DISTINCT
                        CASE
                            WHEN length(lower(btrim("Mode"))) <= 80 THEN lower(btrim("Mode"))
                            ELSE 'legacy-' || md5("Mode")
                        END AS key,
                        left(btrim("Mode"), 120) AS label
                    FROM "Segments"
                    WHERE "Mode" IS NOT NULL AND btrim("Mode") <> ''
                )
                INSERT INTO "TransportProfiles"
                    ("Id", "Key", "Label", "Category", "PlanningSpeedKmh", "SortOrder", "IsActive", "Description", "IsSeeded")
                SELECT
                    (substr(md5('transport-profile:' || key), 1, 8) || '-' ||
                     substr(md5('transport-profile:' || key), 9, 4) || '-' ||
                     substr(md5('transport-profile:' || key), 13, 4) || '-' ||
                     substr(md5('transport-profile:' || key), 17, 4) || '-' ||
                     substr(md5('transport-profile:' || key), 21, 12))::uuid,
                    key,
                    'Legacy: ' || label,
                    'Legacy',
                    NULL,
                    10000,
                    FALSE,
                    'Inactive compatibility profile preserving a pre-catalog segment mode.',
                    TRUE
                FROM legacy
                ON CONFLICT ("Key") DO NOTHING;

                UPDATE "Segments" AS segment
                SET "TransportProfileId" = profile."Id"
                FROM "TransportProfiles" AS profile
                WHERE segment."Mode" IS NOT NULL
                  AND btrim(segment."Mode") <> ''
                  AND profile."Key" = CASE
                      WHEN length(lower(btrim(segment."Mode"))) <= 80 THEN lower(btrim(segment."Mode"))
                      ELSE 'legacy-' || md5(segment."Mode")
                  END;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_Segments_TransportProfiles_TransportProfileId",
                table: "Segments",
                column: "TransportProfileId",
                principalTable: "TransportProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Segments_TransportProfiles_TransportProfileId",
                table: "Segments");

            migrationBuilder.DropTable(
                name: "TransportProfiles");

            migrationBuilder.DropIndex(
                name: "IX_Segments_TransportProfileId",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "TransportProfileId",
                table: "Segments");

            migrationBuilder.AlterColumn<string>(
                name: "Mode",
                table: "Segments",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
