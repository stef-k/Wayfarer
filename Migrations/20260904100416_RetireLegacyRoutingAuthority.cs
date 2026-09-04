using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class RetireLegacyRoutingAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ApplicationSettings_RoutingProviderConfigurations_ActiveRou~",
                table: "ApplicationSettings");

            migrationBuilder.DropIndex(
                name: "IX_ApplicationSettings_ActiveRoutingProviderConfigurationId",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "ActiveRoutingProviderConfigurationId",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "ExternalRouteGenerationEnabled",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "ExternalRouteGenerationVersion",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "ApplicationSettings");

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_AspNetUsers_CreateDefaultRoutingConfiguration" ON "AspNetUsers";
                DROP FUNCTION IF EXISTS "CreateDefaultUserRoutingConfiguration"();
                """);

            migrationBuilder.DropTable(
                name: "UserRoutingConfigurations");

            migrationBuilder.DropTable(
                name: "RoutingProviderProfileMappings");

            migrationBuilder.DropTable(
                name: "RoutingProviderConfigurations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ActiveRoutingProviderConfigurationId",
                table: "ApplicationSettings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ExternalRouteGenerationEnabled",
                table: "ApplicationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ExternalRouteGenerationVersion",
                table: "ApplicationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "ApplicationSettings",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.CreateTable(
                name: "RoutingProviderConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AdapterType = table.Column<int>(type: "integer", nullable: false),
                    Attribution = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    BaseEndpoint = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ConfigurationVersion = table.Column<int>(type: "integer", nullable: false),
                    CredentialCiphertext = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    CredentialPresent = table.Column<bool>(type: "boolean", nullable: false),
                    CredentialRequired = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ExternalCoordinateDisclosure = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    GenerationTimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    MaxConcurrency = table.Column<int>(type: "integer", nullable: false),
                    MinimumIntervalMilliseconds = table.Column<int>(type: "integer", nullable: false, defaultValue: 1000),
                    PersonalRoutingAccess = table.Column<int>(type: "integer", nullable: false),
                    RequestsPerMinute = table.Column<int>(type: "integer", nullable: false),
                    ResponseSizeLimitBytes = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    VerificationFromLatitude = table.Column<double>(type: "double precision", nullable: true),
                    VerificationFromLongitude = table.Column<double>(type: "double precision", nullable: true),
                    VerificationResult = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    VerificationStatus = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    VerificationToLatitude = table.Column<double>(type: "double precision", nullable: true),
                    VerificationToLongitude = table.Column<double>(type: "double precision", nullable: true),
                    VerifiedConfigurationVersion = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoutingProviderConfigurations", x => x.Id);
                    table.CheckConstraint("CK_RoutingProviderConfigurations_MinimumIntervalMilliseconds", "\"MinimumIntervalMilliseconds\" >= 0 AND \"MinimumIntervalMilliseconds\" <= 60000");
                    table.CheckConstraint("CK_RoutingProviderConfigurations_PersonalRoutingAccess", "\"PersonalRoutingAccess\" IN (0, 1, 2)");
                });

            migrationBuilder.CreateTable(
                name: "RoutingProviderProfileMappings",
                columns: table => new
                {
                    RoutingProviderConfigurationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TransportProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    OsrmProfile = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoutingProviderProfileMappings", x => new { x.RoutingProviderConfigurationId, x.TransportProfileId });
                    table.ForeignKey(
                        name: "FK_RoutingProviderProfileMappings_RoutingProviderConfiguration~",
                        column: x => x.RoutingProviderConfigurationId,
                        principalTable: "RoutingProviderConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RoutingProviderProfileMappings_TransportProfiles_TransportP~",
                        column: x => x.TransportProfileId,
                        principalTable: "TransportProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserRoutingConfigurations",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    SelectedProviderConfigurationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConfigurationVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    CredentialCiphertext = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    CredentialPresent = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    VerificationStatus = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    VerifiedProviderConfigurationVersion = table.Column<int>(type: "integer", nullable: true),
                    VerifiedUserConfigurationVersion = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoutingConfigurations", x => x.UserId);
                    table.CheckConstraint("CK_UserRoutingConfigurations_CredentialConsistency", "(\"CredentialPresent\" AND \"CredentialCiphertext\" IS NOT NULL) OR (NOT \"CredentialPresent\" AND \"CredentialCiphertext\" IS NULL)");
                    table.CheckConstraint("CK_UserRoutingConfigurations_DefaultMode", "\"SelectedProviderConfigurationId\" IS NOT NULL OR (NOT \"CredentialPresent\" AND \"CredentialCiphertext\" IS NULL AND \"VerifiedUserConfigurationVersion\" IS NULL AND \"VerifiedProviderConfigurationVersion\" IS NULL AND \"VerificationStatus\" IS NULL)");
                    table.CheckConstraint("CK_UserRoutingConfigurations_VerifiedPair", "(\"VerifiedUserConfigurationVersion\" IS NULL AND \"VerifiedProviderConfigurationVersion\" IS NULL) OR (\"VerifiedUserConfigurationVersion\" IS NOT NULL AND \"VerifiedProviderConfigurationVersion\" IS NOT NULL)");
                    table.CheckConstraint("CK_UserRoutingConfigurations_Version", "\"ConfigurationVersion\" >= 1");
                    table.ForeignKey(
                        name: "FK_UserRoutingConfigurations_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserRoutingConfigurations_RoutingProviderConfigurations_Sel~",
                        column: x => x.SelectedProviderConfigurationId,
                        principalTable: "RoutingProviderConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationSettings_ActiveRoutingProviderConfigurationId",
                table: "ApplicationSettings",
                column: "ActiveRoutingProviderConfigurationId");

            migrationBuilder.CreateIndex(
                name: "IX_RoutingProviderProfileMappings_TransportProfileId",
                table: "RoutingProviderProfileMappings",
                column: "TransportProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoutingConfigurations_SelectedProviderConfigurationId",
                table: "UserRoutingConfigurations",
                column: "SelectedProviderConfigurationId");

            migrationBuilder.AddForeignKey(
                name: "FK_ApplicationSettings_RoutingProviderConfigurations_ActiveRou~",
                table: "ApplicationSettings",
                column: "ActiveRoutingProviderConfigurationId",
                principalTable: "RoutingProviderConfigurations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION "CreateDefaultUserRoutingConfiguration"() RETURNS trigger AS $$
                BEGIN
                    INSERT INTO "UserRoutingConfigurations"
                        ("UserId", "CredentialPresent", "ConfigurationVersion", "CreatedAt", "UpdatedAt")
                    VALUES (NEW."Id", FALSE, 1, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
                    ON CONFLICT ("UserId") DO NOTHING;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS "TR_AspNetUsers_CreateDefaultRoutingConfiguration" ON "AspNetUsers";
                CREATE TRIGGER "TR_AspNetUsers_CreateDefaultRoutingConfiguration"
                AFTER INSERT ON "AspNetUsers"
                FOR EACH ROW EXECUTE FUNCTION "CreateDefaultUserRoutingConfiguration"();
                """);
        }
    }
}
