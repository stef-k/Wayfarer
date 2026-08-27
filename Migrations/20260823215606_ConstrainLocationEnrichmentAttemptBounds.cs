using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class ConstrainLocationEnrichmentAttemptBounds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_LocationEnrichmentAttempt_Generations",
                table: "LocationEnrichmentAttempts",
                sql: "\"CredentialGeneration\" >= 0 AND \"ConfigurationGeneration\" >= 0 AND \"SelectionGeneration\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LocationEnrichmentAttempt_Outcome",
                table: "LocationEnrichmentAttempts",
                sql: "\"Outcome\" IN ('None','NoCandidates','BudgetExhausted','AuthorityUnavailable','RetryableFailure','InvalidCoordinates','NoResult','AttemptLimit','DataFailure')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LocationEnrichmentAttempt_Provider",
                table: "LocationEnrichmentAttempts",
                sql: "\"ProviderKey\" IN ('', 'geoapify', 'mapbox')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_LocationEnrichmentAttempt_Generations",
                table: "LocationEnrichmentAttempts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LocationEnrichmentAttempt_Outcome",
                table: "LocationEnrichmentAttempts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LocationEnrichmentAttempt_Provider",
                table: "LocationEnrichmentAttempts");
        }
    }
}
