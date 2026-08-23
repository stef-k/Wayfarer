using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonalLocationProviderFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GeoapifyUsageGuards",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreditLimit = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeoapifyUsageGuards", x => x.UserId);
                    table.CheckConstraint("CK_GeoapifyUsageGuard_Limit", "\"CreditLimit\" >= 0");
                    table.ForeignKey(
                        name: "FK_GeoapifyUsageGuards_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MapboxProductMeters",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Product = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Limit = table.Column<int>(type: "integer", nullable: false),
                    CycleStart = table.Column<DateOnly>(type: "date", nullable: false),
                    AdmittedCount = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapboxProductMeters", x => new { x.UserId, x.Product });
                    table.CheckConstraint("CK_MapboxProductMeter_Counts", "\"Limit\" >= 0 AND \"AdmittedCount\" >= 0");
                    table.CheckConstraint("CK_MapboxProductMeter_Product", "\"Product\" IN (3, 4)");
                    table.ForeignKey(
                        name: "FK_MapboxProductMeters_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PersonalLocationProviderProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ProtectedCredential = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    CredentialGeneration = table.Column<int>(type: "integer", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    GeocodingAuthorized = table.Column<bool>(type: "boolean", nullable: false),
                    RoutingAuthorized = table.Column<bool>(type: "boolean", nullable: false),
                    GeocodingGeneration = table.Column<int>(type: "integer", nullable: false),
                    RoutingGeneration = table.Column<int>(type: "integer", nullable: false),
                    GeocodingVerification = table.Column<int>(type: "integer", nullable: false),
                    RoutingVerification = table.Column<int>(type: "integer", nullable: false),
                    GeocodingVerifiedCredentialGeneration = table.Column<int>(type: "integer", nullable: true),
                    GeocodingVerifiedConfigurationGeneration = table.Column<int>(type: "integer", nullable: true),
                    RoutingVerifiedCredentialGeneration = table.Column<int>(type: "integer", nullable: true),
                    RoutingVerifiedConfigurationGeneration = table.Column<int>(type: "integer", nullable: true),
                    LegacyMigrationState = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonalLocationProviderProfiles", x => x.Id);
                    table.CheckConstraint("CK_PersonalProvider_Generations", "\"CredentialGeneration\" > 0 AND \"GeocodingGeneration\" > 0 AND \"RoutingGeneration\" > 0");
                    table.CheckConstraint("CK_PersonalProvider_Provider", "\"ProviderKey\" IN ('geoapify', 'mapbox')");
                    table.CheckConstraint("CK_PersonalProvider_Verification", "\"GeocodingVerification\" BETWEEN 0 AND 3 AND \"RoutingVerification\" BETWEEN 0 AND 3");
                    table.ForeignKey(
                        name: "FK_PersonalLocationProviderProfiles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PersonalLocationProviderSelections",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    GeocodingProviderKey = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    RoutingProviderKey = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    GeocodingSelectionGeneration = table.Column<int>(type: "integer", nullable: false),
                    RoutingSelectionGeneration = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonalLocationProviderSelections", x => x.UserId);
                    table.CheckConstraint("CK_PersonalProviderSelection_Geocoding", "\"GeocodingProviderKey\" IS NULL OR \"GeocodingProviderKey\" IN ('geoapify', 'mapbox')");
                    table.CheckConstraint("CK_PersonalProviderSelection_Routing", "\"RoutingProviderKey\" IS NULL OR \"RoutingProviderKey\" IN ('geoapify', 'mapbox')");
                    table.ForeignKey(
                        name: "FK_PersonalLocationProviderSelections_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GeoapifyUsageAdmissions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Credits = table.Column<int>(type: "integer", nullable: false),
                    Product = table.Column<int>(type: "integer", nullable: false),
                    AdmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeoapifyUsageAdmissions", x => x.Id);
                    table.CheckConstraint("CK_GeoapifyUsageAdmission_Credits", "\"Credits\" > 0");
                    table.CheckConstraint("CK_GeoapifyUsageAdmission_Product", "\"Product\" IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_GeoapifyUsageAdmissions_GeoapifyUsageGuards_UserId",
                        column: x => x.UserId,
                        principalTable: "GeoapifyUsageGuards",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GeoapifyUsageAdmissions_UserId_AdmittedAt",
                table: "GeoapifyUsageAdmissions",
                columns: new[] { "UserId", "AdmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PersonalLocationProviderProfiles_UserId_ProviderKey",
                table: "PersonalLocationProviderProfiles",
                columns: new[] { "UserId", "ProviderKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GeoapifyUsageAdmissions");

            migrationBuilder.DropTable(
                name: "MapboxProductMeters");

            migrationBuilder.DropTable(
                name: "PersonalLocationProviderProfiles");

            migrationBuilder.DropTable(
                name: "PersonalLocationProviderSelections");

            migrationBuilder.DropTable(
                name: "GeoapifyUsageGuards");
        }
    }
}
