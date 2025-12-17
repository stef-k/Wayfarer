using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class PreventDuplicatePendingInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GroupInvitations_GroupId",
                table: "GroupInvitations");

            migrationBuilder.CreateIndex(
                name: "IX_GroupInvitation_GroupId_InviteeUserId_Pending",
                table: "GroupInvitations",
                columns: new[] { "GroupId", "InviteeUserId" },
                unique: true,
                filter: "\"Status\" = 'Pending' AND \"InviteeUserId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GroupInvitation_GroupId_InviteeUserId_Pending",
                table: "GroupInvitations");

            migrationBuilder.CreateIndex(
                name: "IX_GroupInvitations_GroupId",
                table: "GroupInvitations",
                column: "GroupId");
        }
    }
}
