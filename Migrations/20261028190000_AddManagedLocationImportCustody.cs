using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddManagedLocationImportCustody : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnrichmentHandoffPending",
                table: "LocationImports",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "FailureCode",
                table: "LocationImports",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ManagedUpload",
                table: "LocationImports",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ManagedImportStore",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Identity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagedImportStore", x => x.Id);
                    table.CheckConstraint("CK_ManagedImportStore_Singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "MobileLocationImportUpload",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: true),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    Format = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    ByteLength = table.Column<long>(type: "bigint", nullable: true),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EnrichmentRequested = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AcceptedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetiredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ImportId = table.Column<int>(type: "integer", nullable: true),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    FileReference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FileState = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ReservedBytes = table.Column<long>(type: "bigint", nullable: false),
                    VerifiedBytes = table.Column<long>(type: "bigint", nullable: false),
                    OwnerByteLimit = table.Column<long>(type: "bigint", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UploadDeadlineUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CleanupReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CleanupEligibleAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetryAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastState = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LastProcessedRecords = table.Column<int>(type: "integer", nullable: false),
                    LastTotalRecords = table.Column<int>(type: "integer", nullable: true),
                    LastExecutionEpoch = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MobileLocationImportUpload", x => x.Id);
                    table.CheckConstraint("CK_ManagedUpload_Owner", "(\"UserId\" IS NOT NULL AND \"RequestId\" IS NOT NULL AND \"Format\" IS NOT NULL AND \"ByteLength\" IS NOT NULL AND \"Sha256\" IS NOT NULL AND \"Format\" IN ('csv','wayfarer-geojson') AND \"ByteLength\" > 0 AND \"Sha256\" ~ '^[0-9a-f]{64}$') OR (\"UserId\" IS NULL AND \"RequestId\" IS NULL AND \"CleanupReason\" = 'account_deleted')");
                    table.CheckConstraint("CK_ManagedUpload_Reservation", "\"ReservedBytes\" >= 0 AND \"VerifiedBytes\" >= 0 AND \"Generation\" >= 0 AND ((\"FileState\" = 'Removed' AND \"ReservedBytes\" = 0 AND \"FileReference\" IS NULL) OR (\"FileState\" <> 'Removed' AND \"FileReference\" IS NOT NULL AND \"ReservedBytes\" > 0 AND \"FileReference\" ~ '^imports/[0-9a-f]{32}\\.(csv|geojson)$'))");
                    table.CheckConstraint("CK_ManagedUpload_State", "\"FileState\" IN ('Uploading','Retained','CleanupClaimed','Removed')");
                    table.ForeignKey(
                        name: "FK_MobileLocationImportUpload_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MobileLocationImportUpload_LocationImports_ImportId",
                        column: x => x.ImportId,
                        principalTable: "LocationImports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManagedImport_Reconciliation",
                table: "LocationImports",
                column: "Id",
                filter: "\"ManagedUpload\" AND (\"ProjectionPending\" OR \"DeletionRequestedAtUtc\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "UX_ManagedImport_ActiveOwner",
                table: "LocationImports",
                column: "UserId",
                unique: true,
                filter: "\"ManagedUpload\" AND \"Status\" IN ('In Progress','Stopping')");

            migrationBuilder.CreateIndex(
                name: "IX_MobileLocationImportUpload_FileReference",
                table: "MobileLocationImportUpload",
                column: "FileReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MobileLocationImportUpload_FileState_CleanupEligibleAtUtc_Id",
                table: "MobileLocationImportUpload",
                columns: new[] { "FileState", "CleanupEligibleAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_MobileLocationImportUpload_FileState_LeaseExpiresAtUtc_Id",
                table: "MobileLocationImportUpload",
                columns: new[] { "FileState", "LeaseExpiresAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_MobileLocationImportUpload_ImportId",
                table: "MobileLocationImportUpload",
                column: "ImportId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MobileLocationImportUpload_RetryAtUtc_Id",
                table: "MobileLocationImportUpload",
                columns: new[] { "RetryAtUtc", "Id" },
                filter: "\"RetryAtUtc\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MobileLocationImportUpload_UserId_CreatedAtUtc",
                table: "MobileLocationImportUpload",
                columns: new[] { "UserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MobileLocationImportUpload_UserId_RequestId",
                table: "MobileLocationImportUpload",
                columns: new[] { "UserId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ManagedUpload_LiveOwner",
                table: "MobileLocationImportUpload",
                column: "UserId",
                unique: true,
                filter: "\"FileState\" = 'Uploading'");
            // Reservations, immutable custody and account/import deletion must survive every EF cascade.
            migrationBuilder.Sql("""
                -- All cooperating mutations use gate (if needed) -> installation -> account -> custody -> import.
                -- These triggers retain invariants even when EF cascades or a database client changes a row directly.
                CREATE FUNCTION wayfarer_managed_upload_guard() RETURNS trigger LANGUAGE plpgsql AS $body$
                DECLARE owner_files integer; owner_bytes bigint; installation_bytes bigint; allocations integer;
                BEGIN
                    PERFORM pg_advisory_xact_lock(887, 1);
                    IF TG_OP = 'DELETE' THEN
                        IF OLD."FileState" <> 'Removed' THEN
                            RAISE EXCEPTION 'managed custody requires confirmed removal' USING ERRCODE = '23514';
                        END IF;
                        RETURN OLD;
                    END IF;
                    IF TG_OP = 'UPDATE' AND NEW."UserId" IS NOT NULL THEN
                        IF ROW(NEW."UserId", NEW."RequestId", NEW."Format", NEW."ByteLength", NEW."Sha256", NEW."EnrichmentRequested", NEW."CreatedAtUtc")
                            IS DISTINCT FROM ROW(OLD."UserId", OLD."RequestId", OLD."Format", OLD."ByteLength", OLD."Sha256", OLD."EnrichmentRequested", OLD."CreatedAtUtc") THEN
                            RAISE EXCEPTION 'managed request fingerprint is immutable' USING ERRCODE = '23514';
                        END IF;
                        IF OLD."AcceptedAtUtc" IS NOT NULL AND NEW."AcceptedAtUtc" IS DISTINCT FROM OLD."AcceptedAtUtc" THEN
                            RAISE EXCEPTION 'accepted identity is immutable' USING ERRCODE = '23514';
                        END IF;
                        IF NEW."Generation" IS DISTINCT FROM OLD."Generation" OR NEW."FileReference" IS DISTINCT FROM OLD."FileReference" THEN
                            IF NOT ((OLD."FileState" = 'Removed' AND OLD."AcceptedAtUtc" IS NULL AND NEW."Generation" = OLD."Generation" + 1 AND NEW."FileState" = 'Uploading')
                                OR (OLD."FileState" = 'CleanupClaimed' AND NEW."FileState" = 'Removed' AND NEW."FileReference" IS NULL AND NEW."Generation" = OLD."Generation")) THEN
                                RAISE EXCEPTION 'managed current file is immutable until removal' USING ERRCODE = '23514';
                            END IF;
                        END IF;
                        IF (OLD."FileState" = 'CleanupClaimed' AND NEW."FileState" NOT IN ('CleanupClaimed','Removed'))
                            OR (OLD."FileState" = 'Removed' AND OLD."AcceptedAtUtc" IS NOT NULL AND NEW."FileState" <> 'Removed') THEN
                            RAISE EXCEPTION 'managed cleanup cannot be reversed' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    IF NEW."ReservedBytes" > 0 AND (TG_OP = 'INSERT' OR NEW."ReservedBytes" > OLD."ReservedBytes") THEN
                        SELECT count(*), coalesce(sum("ReservedBytes"),0) INTO owner_files, owner_bytes
                            FROM "MobileLocationImportUpload" WHERE "UserId" = NEW."UserId" AND "Id" <> NEW."Id" AND "FileState" <> 'Removed';
                        SELECT coalesce(sum("ReservedBytes"),0) INTO installation_bytes FROM "MobileLocationImportUpload" WHERE "Id" <> NEW."Id";
                        IF owner_files >= 3 OR owner_bytes + NEW."ReservedBytes" > NEW."OwnerByteLimit"
                            OR installation_bytes + NEW."ReservedBytes" > 10737418240 THEN
                            RAISE EXCEPTION 'managed reservation limit' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        SELECT count(*) INTO allocations FROM "MobileLocationImportUpload"
                            WHERE "UserId" = NEW."UserId" AND "CreatedAtUtc" > clock_timestamp() - interval '24 hours';
                        IF allocations >= 100 THEN RAISE EXCEPTION 'managed allocation limit' USING ERRCODE = '23514'; END IF;
                    END IF;
                    RETURN NEW;
                END $body$;
                CREATE TRIGGER managed_upload_guard BEFORE INSERT OR UPDATE OR DELETE ON "MobileLocationImportUpload"
                    FOR EACH ROW EXECUTE FUNCTION wayfarer_managed_upload_guard();

                -- Identity deletion and its cascades erase personal request/result data atomically, preserving only pending cleanup.
                CREATE FUNCTION wayfarer_managed_account_erasure() RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                    PERFORM pg_advisory_xact_lock(887, 1);
                    PERFORM pg_advisory_xact_lock(hashtextextended('location-deletion:' || OLD."Id", 838));
                    DELETE FROM "MobileLocationImportUpload" WHERE "UserId" = OLD."Id" AND "FileState" = 'Removed';
                    UPDATE "MobileLocationImportUpload" SET
                        "UserId" = NULL, "RequestId" = NULL, "Format" = NULL, "ByteLength" = NULL, "Sha256" = NULL,
                        "EnrichmentRequested" = false, "CreatedAtUtc" = 'epoch', "AcceptedAtUtc" = NULL, "RetiredAtUtc" = NULL,
                        "ImportId" = NULL, "VerifiedBytes" = 0, "OwnerByteLimit" = 0, "LeaseId" = NULL,
                        "LeaseExpiresAtUtc" = NULL, "UploadDeadlineUtc" = NULL, "CleanupEligibleAtUtc" = NULL,
                        "LastState" = NULL, "LastProcessedRecords" = 0, "LastTotalRecords" = NULL, "LastExecutionEpoch" = 0,
                        "FileState" = 'CleanupClaimed', "CleanupReason" = 'account_deleted', "FailureCode" = NULL, "RetryAtUtc" = clock_timestamp()
                        WHERE "UserId" = OLD."Id";
                    RETURN OLD;
                END $body$;
                CREATE TRIGGER managed_account_erasure BEFORE DELETE ON "AspNetUsers"
                    FOR EACH ROW EXECUTE FUNCTION wayfarer_managed_account_erasure();

                -- SET NULL preserves custody on recorded import deletion, including the last aggregate result.
                CREATE FUNCTION wayfarer_managed_import_deletion() RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                    IF NOT OLD."ManagedUpload" THEN RETURN OLD; END IF;
                    PERFORM pg_advisory_xact_lock(887, 1);
                    IF EXISTS (SELECT 1 FROM "MobileLocationImportUpload" WHERE "ImportId" = OLD."Id")
                        AND (OLD."Status" IN ('In Progress','Stopping') OR OLD."EnrichmentHandoffPending") THEN
                        RAISE EXCEPTION 'managed import has unresolved execution or handoff' USING ERRCODE = '23514';
                    END IF;
                    UPDATE "MobileLocationImportUpload" SET
                        "RetiredAtUtc" = clock_timestamp(), "LastState" = OLD."Status", "LastProcessedRecords" = OLD."LastProcessedIndex",
                        "LastTotalRecords" = CASE WHEN OLD."TotalRecords" > 0 OR OLD."Status" = 'Completed' THEN OLD."TotalRecords" ELSE NULL END,
                        "LastExecutionEpoch" = OLD."ExecutionEpoch", "CleanupReason" = 'import_deleted',
                        "FileState" = CASE WHEN "FileState" = 'Removed' THEN 'Removed' ELSE 'CleanupClaimed' END,
                        "RetryAtUtc" = CASE WHEN "FileState" = 'Removed' THEN NULL ELSE clock_timestamp() END
                        WHERE "ImportId" = OLD."Id";
                    RETURN OLD;
                END $body$;
                CREATE TRIGGER managed_import_deletion BEFORE DELETE ON "LocationImports"
                    FOR EACH ROW EXECUTE FUNCTION wayfarer_managed_import_deletion();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping live custody would strand payloads. Drain managed history/files before an explicit downgrade.
            migrationBuilder.Sql("""
                DO $body$ BEGIN
                    IF EXISTS (SELECT 1 FROM "MobileLocationImportUpload") THEN
                        RAISE EXCEPTION 'drain managed import custody before downgrade';
                    END IF;
                END $body$;
                DROP TRIGGER managed_import_deletion ON "LocationImports";
                DROP FUNCTION wayfarer_managed_import_deletion();
                DROP TRIGGER managed_account_erasure ON "AspNetUsers";
                DROP FUNCTION wayfarer_managed_account_erasure();
                DROP TRIGGER managed_upload_guard ON "MobileLocationImportUpload";
                DROP FUNCTION wayfarer_managed_upload_guard();
                """);
            migrationBuilder.DropTable(
                name: "ManagedImportStore");

            migrationBuilder.DropTable(
                name: "MobileLocationImportUpload");

            migrationBuilder.DropIndex(
                name: "IX_ManagedImport_Reconciliation",
                table: "LocationImports");

            migrationBuilder.DropIndex(
                name: "UX_ManagedImport_ActiveOwner",
                table: "LocationImports");

            migrationBuilder.DropColumn(
                name: "EnrichmentHandoffPending",
                table: "LocationImports");

            migrationBuilder.DropColumn(
                name: "FailureCode",
                table: "LocationImports");

            migrationBuilder.DropColumn(
                name: "ManagedUpload",
                table: "LocationImports");

        }
    }
}
