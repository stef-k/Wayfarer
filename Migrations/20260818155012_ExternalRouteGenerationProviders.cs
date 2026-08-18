using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class ExternalRouteGenerationProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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

            migrationBuilder.CreateTable(
                name: "RoutingProviderConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    AdapterType = table.Column<int>(type: "integer", nullable: false),
                    BaseEndpoint = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CredentialCiphertext = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    CredentialPresent = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Attribution = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ExternalCoordinateDisclosure = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    VerificationFromLongitude = table.Column<double>(type: "double precision", nullable: true),
                    VerificationFromLatitude = table.Column<double>(type: "double precision", nullable: true),
                    VerificationToLongitude = table.Column<double>(type: "double precision", nullable: true),
                    VerificationToLatitude = table.Column<double>(type: "double precision", nullable: true),
                    ConfigurationVersion = table.Column<int>(type: "integer", nullable: false),
                    VerifiedConfigurationVersion = table.Column<int>(type: "integer", nullable: true),
                    VerificationStatus = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    VerificationResult = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    GenerationTimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    ResponseSizeLimitBytes = table.Column<int>(type: "integer", nullable: false),
                    RequestsPerMinute = table.Column<int>(type: "integer", nullable: false),
                    MaxConcurrency = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoutingProviderConfigurations", x => x.Id);
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

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationSettings_ActiveRoutingProviderConfigurationId",
                table: "ApplicationSettings",
                column: "ActiveRoutingProviderConfigurationId");

            migrationBuilder.CreateIndex(
                name: "IX_RoutingProviderProfileMappings_TransportProfileId",
                table: "RoutingProviderProfileMappings",
                column: "TransportProfileId");

            migrationBuilder.AddForeignKey(
                name: "FK_ApplicationSettings_RoutingProviderConfigurations_ActiveRou~",
                table: "ApplicationSettings",
                column: "ActiveRoutingProviderConfigurationId",
                principalTable: "RoutingProviderConfigurations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ApplicationSettings_RoutingProviderConfigurations_ActiveRou~",
                table: "ApplicationSettings");

            migrationBuilder.DropTable(
                name: "RoutingProviderProfileMappings");

            migrationBuilder.DropTable(
                name: "RoutingProviderConfigurations");

            migrationBuilder.DropIndex(
                name: "IX_ApplicationSettings_ActiveRoutingProviderConfigurationId",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "ActiveRoutingProviderConfigurationId",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "ExternalRouteGenerationEnabled",
                table: "ApplicationSettings");
        }
    }
}
