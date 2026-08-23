using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class EnforcePersonalProviderSelectionIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_PersonalLocationProviderProfiles_UserId_ProviderKey",
                table: "PersonalLocationProviderProfiles",
                columns: new[] { "UserId", "ProviderKey" });

            migrationBuilder.CreateIndex(
                name: "IX_PersonalLocationProviderSelections_UserId_GeocodingProvider~",
                table: "PersonalLocationProviderSelections",
                columns: new[] { "UserId", "GeocodingProviderKey" });

            migrationBuilder.CreateIndex(
                name: "IX_PersonalLocationProviderSelections_UserId_RoutingProviderKey",
                table: "PersonalLocationProviderSelections",
                columns: new[] { "UserId", "RoutingProviderKey" });

            migrationBuilder.AddForeignKey(
                name: "FK_PersonalLocationProviderSelections_PersonalLocationProvider~",
                table: "PersonalLocationProviderSelections",
                columns: new[] { "UserId", "GeocodingProviderKey" },
                principalTable: "PersonalLocationProviderProfiles",
                principalColumns: new[] { "UserId", "ProviderKey" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PersonalLocationProviderSelections_PersonalLocationProvide~1",
                table: "PersonalLocationProviderSelections",
                columns: new[] { "UserId", "RoutingProviderKey" },
                principalTable: "PersonalLocationProviderProfiles",
                principalColumns: new[] { "UserId", "ProviderKey" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PersonalLocationProviderSelections_PersonalLocationProvider~",
                table: "PersonalLocationProviderSelections");

            migrationBuilder.DropForeignKey(
                name: "FK_PersonalLocationProviderSelections_PersonalLocationProvide~1",
                table: "PersonalLocationProviderSelections");

            migrationBuilder.DropIndex(
                name: "IX_PersonalLocationProviderSelections_UserId_GeocodingProvider~",
                table: "PersonalLocationProviderSelections");

            migrationBuilder.DropIndex(
                name: "IX_PersonalLocationProviderSelections_UserId_RoutingProviderKey",
                table: "PersonalLocationProviderSelections");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_PersonalLocationProviderProfiles_UserId_ProviderKey",
                table: "PersonalLocationProviderProfiles");
        }
    }
}
