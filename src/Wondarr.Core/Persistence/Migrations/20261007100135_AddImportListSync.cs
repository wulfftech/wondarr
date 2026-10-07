using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wondarr.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddImportListSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "position",
                table: "import_list_item",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "removed_at",
                table: "import_list_item",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "enabled",
                table: "import_list",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "last_sync_message",
                table: "import_list",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_text",
                table: "import_list",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "sync_interval_hours",
                table: "import_list",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Pasted lists were written in line order, so their ids order them: keep that order.
            migrationBuilder.Sql("UPDATE import_list_item SET position = id;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "position",
                table: "import_list_item");

            migrationBuilder.DropColumn(
                name: "removed_at",
                table: "import_list_item");

            migrationBuilder.DropColumn(
                name: "enabled",
                table: "import_list");

            migrationBuilder.DropColumn(
                name: "last_sync_message",
                table: "import_list");

            migrationBuilder.DropColumn(
                name: "source_text",
                table: "import_list");

            migrationBuilder.DropColumn(
                name: "sync_interval_hours",
                table: "import_list");
        }
    }
}
