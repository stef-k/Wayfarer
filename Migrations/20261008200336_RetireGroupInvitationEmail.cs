using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <summary>
    /// Retires unresolved pending invitations without guessing an account identity, then
    /// permanently discards obsolete invitation email values while retaining other history.
    /// </summary>
    public partial class RetireGroupInvitationEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "GroupInvitations"
                SET "Status" = 'Revoked', "RespondedAt" = CURRENT_TIMESTAMP
                WHERE "InviteeUserId" IS NULL AND "Status" = 'Pending';
                """);

            migrationBuilder.DropColumn(
                name: "InviteeEmail",
                table: "GroupInvitations");
        }

        /// <summary>Restores an empty nullable column; discarded email values and revocations are irreversible.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InviteeEmail",
                table: "GroupInvitations",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }
    }
}
