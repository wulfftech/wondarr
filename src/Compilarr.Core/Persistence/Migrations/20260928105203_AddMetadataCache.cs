using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Compilarr.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMetadataCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "metadata_cache",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    provider = table.Column<string>(type: "TEXT", nullable: false),
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    payload = table.Column<string>(type: "TEXT", nullable: false),
                    fetched_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_metadata_cache", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_metadata_cache_expires_at",
                table: "metadata_cache",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_metadata_cache_provider_key",
                table: "metadata_cache",
                columns: new[] { "provider", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "metadata_cache");
        }
    }
}
