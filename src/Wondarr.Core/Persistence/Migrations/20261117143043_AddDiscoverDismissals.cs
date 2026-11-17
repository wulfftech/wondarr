using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wondarr.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscoverDismissals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "discover_dismissal",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    mb_recording_id = table.Column<string>(type: "TEXT", nullable: true),
                    artist_key = table.Column<string>(type: "TEXT", nullable: false),
                    title_key = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discover_dismissal", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_discover_dismissal_artist_key_title_key",
                table: "discover_dismissal",
                columns: new[] { "artist_key", "title_key" });

            migrationBuilder.CreateIndex(
                name: "ix_discover_dismissal_mb_recording_id",
                table: "discover_dismissal",
                column: "mb_recording_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discover_dismissal");
        }
    }
}
