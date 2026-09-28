using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Compilarr.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHistoryAndBlocklist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blocklist",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    song_id = table.Column<long>(type: "INTEGER", nullable: true),
                    source_type = table.Column<string>(type: "TEXT", nullable: false),
                    blocklist_key = table.Column<string>(type: "TEXT", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blocklist", x => x.id);
                    table.ForeignKey(
                        name: "fk_blocklist_songs_song_id",
                        column: x => x.song_id,
                        principalTable: "song",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "history",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    song_id = table.Column<long>(type: "INTEGER", nullable: false),
                    event_type = table.Column<string>(type: "TEXT", nullable: false),
                    source_instance_id = table.Column<long>(type: "INTEGER", nullable: true),
                    quality_id = table.Column<long>(type: "INTEGER", nullable: true),
                    data = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}"),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_history_qualities_quality_id",
                        column: x => x.quality_id,
                        principalTable: "quality",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_history_songs_song_id",
                        column: x => x.song_id,
                        principalTable: "song",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_blocklist_song_id",
                table: "blocklist",
                column: "song_id");

            migrationBuilder.CreateIndex(
                name: "ix_blocklist_source_type_blocklist_key",
                table: "blocklist",
                columns: new[] { "source_type", "blocklist_key" });

            migrationBuilder.CreateIndex(
                name: "ix_history_created_at",
                table: "history",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_history_quality_id",
                table: "history",
                column: "quality_id");

            migrationBuilder.CreateIndex(
                name: "ix_history_song_id",
                table: "history",
                column: "song_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blocklist");

            migrationBuilder.DropTable(
                name: "history");
        }
    }
}
