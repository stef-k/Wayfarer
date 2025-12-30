using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddTokenHashToApiToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Token",
                table: "ApiTokens",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "TokenHash",
                table: "ApiTokens",
                type: "text",
                nullable: true);

            // Data migration: Hash existing Wayfarer-generated tokens (those starting with 'wf_')
            // Uses PostgreSQL's sha256 and encode functions to create lowercase hex hash
            // After hashing, clear the plain token for security
            migrationBuilder.Sql(@"
                UPDATE ""ApiTokens""
                SET ""TokenHash"" = lower(encode(sha256(convert_to(""Token"", 'UTF8')), 'hex')),
                    ""Token"" = NULL
                WHERE ""Token"" IS NOT NULL
                  AND ""Token"" LIKE 'wf_%';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TokenHash",
                table: "ApiTokens");

            migrationBuilder.AlterColumn<string>(
                name: "Token",
                table: "ApiTokens",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
