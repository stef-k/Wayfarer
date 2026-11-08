using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace Wayfarer.Migrations;

/// <summary>One transactional upgrade of stored anonymous rows; existing identities and all content remain unchanged.</summary>
public partial class AssignStoredLocationKeys : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        """UPDATE "Locations" SET "IdempotencyKey" = gen_random_uuid() WHERE "IdempotencyKey" IS NULL""");

    /// <summary>Removing migration history never erases identities already published to clients or files.</summary>
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
