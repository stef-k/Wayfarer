using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class UserPersonalRoutingCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PersonalRoutingAccess",
                table: "RoutingProviderConfigurations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "UserRoutingConfigurations",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    SelectedProviderConfigurationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CredentialCiphertext = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    CredentialPresent = table.Column<bool>(type: "boolean", nullable: false),
                    ConfigurationVersion = table.Column<int>(type: "integer", nullable: false),
                    VerifiedUserConfigurationVersion = table.Column<int>(type: "integer", nullable: true),
                    VerifiedProviderConfigurationVersion = table.Column<int>(type: "integer", nullable: true),
                    VerificationStatus = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
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

            migrationBuilder.Sql("""
                INSERT INTO "UserRoutingConfigurations"
                    ("UserId", "CredentialPresent", "ConfigurationVersion", "CreatedAt", "UpdatedAt")
                SELECT "Id", FALSE, 1, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                FROM "AspNetUsers"
                ON CONFLICT ("UserId") DO NOTHING;
                """);

            migrationBuilder.Sql("""
                CREATE FUNCTION "CreateDefaultUserRoutingConfiguration"() RETURNS trigger AS $$
                BEGIN
                    INSERT INTO "UserRoutingConfigurations"
                        ("UserId", "CredentialPresent", "ConfigurationVersion", "CreatedAt", "UpdatedAt")
                    VALUES (NEW."Id", FALSE, 1, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER "TR_AspNetUsers_CreateDefaultRoutingConfiguration"
                AFTER INSERT ON "AspNetUsers"
                FOR EACH ROW EXECUTE FUNCTION "CreateDefaultUserRoutingConfiguration"();
                """);

            migrationBuilder.CreateIndex(
                name: "IX_UserRoutingConfigurations_SelectedProviderConfigurationId",
                table: "UserRoutingConfigurations",
                column: "SelectedProviderConfigurationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_AspNetUsers_CreateDefaultRoutingConfiguration" ON "AspNetUsers";
                DROP FUNCTION IF EXISTS "CreateDefaultUserRoutingConfiguration"();
                """);

            migrationBuilder.DropTable(
                name: "UserRoutingConfigurations");

            migrationBuilder.DropColumn(
                name: "PersonalRoutingAccess",
                table: "RoutingProviderConfigurations");
        }
    }
}
