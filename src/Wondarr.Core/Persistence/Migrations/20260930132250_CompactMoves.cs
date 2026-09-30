using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wondarr.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompactMoves : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "compact_move",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    library_id = table.Column<long>(type: "INTEGER", nullable: false),
                    song_id = table.Column<long>(type: "INTEGER", nullable: false),
                    from_path = table.Column<string>(type: "TEXT", nullable: true),
                    staged_path = table.Column<string>(type: "TEXT", nullable: true),
                    to_path = table.Column<string>(type: "TEXT", nullable: true),
                    final_path = table.Column<string>(type: "TEXT", nullable: true),
                    proposed = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}"),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    message = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_compact_move", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_compact_move_library_id_state",
                table: "compact_move",
                columns: new[] { "library_id", "state" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "compact_move");
        }
    }
}
