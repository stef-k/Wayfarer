using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class AddImageCacheMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ImageCacheExpiryDays",
                table: "ApplicationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 90);

            migrationBuilder.AddColumn<int>(
                name: "MaxCacheImageSizeInMB",
                table: "ApplicationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 512);

            migrationBuilder.CreateTable(
                name: "ImageCacheMetadata",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CacheKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FilePath = table.Column<string>(type: "text", nullable: false),
                    Size = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    LastAccessed = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageCacheMetadata", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImageCacheMetadata_CacheKey",
                table: "ImageCacheMetadata",
                column: "CacheKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImageCacheMetadata");

            migrationBuilder.DropColumn(
                name: "ImageCacheExpiryDays",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "MaxCacheImageSizeInMB",
                table: "ApplicationSettings");
        }
    }
}
