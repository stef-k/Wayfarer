using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class ProtectedLocationMutations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OriginalCreateAddressToken",
                table: "Locations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LocationMutationOperations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: false),
                    Uuid = table.Column<Guid>(type: "uuid", nullable: true),
                    BaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Adapter = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Normalization = table.Column<string>(type: "text", nullable: false),
                    Fingerprint = table.Column<string>(type: "text", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AcceptedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExecuteBeforeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceiptRetainUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExecutorNonce = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: true),
                    ResultStatus = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocationMutationOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LocationMutationOperations_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LocationMutationOperations_ReceiptRetainUntilUtc_UserId_Id",
                table: "LocationMutationOperations",
                columns: new[] { "ReceiptRetainUntilUtc", "UserId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_LocationMutationOperations_UserId_BaseId_ActionId",
                table: "LocationMutationOperations",
                columns: new[] { "UserId", "BaseId", "ActionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LocationMutationOperations");

            migrationBuilder.DropColumn(
                name: "OriginalCreateAddressToken",
                table: "Locations");
        }
    }
}
