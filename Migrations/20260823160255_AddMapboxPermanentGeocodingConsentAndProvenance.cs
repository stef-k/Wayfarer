using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddMapboxPermanentGeocodingConsentAndProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AddressEnrichedAt",
                table: "Places",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressEnrichmentProvider",
                table: "Places",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressEnrichmentStorageMode",
                table: "Places",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PermanentGeocodingConsentCredentialGeneration",
                table: "PersonalLocationProviderProfiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PermanentGeocodingConsentVersion",
                table: "PersonalLocationProviderProfiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PermanentGeocodingConsentedAt",
                table: "PersonalLocationProviderProfiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReverseGeocodedAt",
                table: "Locations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReverseGeocodingProvider",
                table: "Locations",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReverseGeocodingStorageMode",
                table: "Locations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AddressEnrichedAt",
                table: "Places");

            migrationBuilder.DropColumn(
                name: "AddressEnrichmentProvider",
                table: "Places");

            migrationBuilder.DropColumn(
                name: "AddressEnrichmentStorageMode",
                table: "Places");

            migrationBuilder.DropColumn(
                name: "PermanentGeocodingConsentCredentialGeneration",
                table: "PersonalLocationProviderProfiles");

            migrationBuilder.DropColumn(
                name: "PermanentGeocodingConsentVersion",
                table: "PersonalLocationProviderProfiles");

            migrationBuilder.DropColumn(
                name: "PermanentGeocodingConsentedAt",
                table: "PersonalLocationProviderProfiles");

            migrationBuilder.DropColumn(
                name: "ReverseGeocodedAt",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "ReverseGeocodingProvider",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "ReverseGeocodingStorageMode",
                table: "Locations");
        }
    }
}
