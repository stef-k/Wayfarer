using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class OrgPeerVisibilityFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GroupType",
                table: "Groups",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OrgPeerVisibilityEnabled",
                table: "Groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "OrgPeerVisibilityAccessDisabled",
                table: "GroupMembers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GroupType",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "OrgPeerVisibilityEnabled",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "OrgPeerVisibilityAccessDisabled",
                table: "GroupMembers");
        }
    }
}
