using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wondarr.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIndexersAndDownloadClients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "download_client",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    type = table.Column<string>(type: "TEXT", nullable: false),
                    protocol = table.Column<string>(type: "TEXT", nullable: false),
                    settings = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}"),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    priority = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_download_client", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "indexer",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    type = table.Column<string>(type: "TEXT", nullable: false),
                    protocol = table.Column<string>(type: "TEXT", nullable: false),
                    settings = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}"),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    priority = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 25),
                    download_client_id = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_indexer", x => x.id);
                    table.ForeignKey(
                        name: "fk_indexer_download_clients_download_client_id",
                        column: x => x.download_client_id,
                        principalTable: "download_client",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_indexer_download_client_id",
                table: "indexer",
                column: "download_client_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "indexer");

            migrationBuilder.DropTable(
                name: "download_client");
        }
    }
}
