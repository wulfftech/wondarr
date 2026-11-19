using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wondarr.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMusicVideoFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "music_video_file",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    path = table.Column<string>(type: "TEXT", nullable: false),
                    size = table.Column<long>(type: "INTEGER", nullable: false),
                    modified_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    nfo_modified_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    container = table.Column<string>(type: "TEXT", nullable: true),
                    duration_ms = table.Column<int>(type: "INTEGER", nullable: true),
                    width = table.Column<int>(type: "INTEGER", nullable: true),
                    height = table.Column<int>(type: "INTEGER", nullable: true),
                    parsed_artist = table.Column<string>(type: "TEXT", nullable: true),
                    parsed_title = table.Column<string>(type: "TEXT", nullable: true),
                    nfo_artist = table.Column<string>(type: "TEXT", nullable: true),
                    nfo_title = table.Column<string>(type: "TEXT", nullable: true),
                    tag_artist = table.Column<string>(type: "TEXT", nullable: true),
                    tag_title = table.Column<string>(type: "TEXT", nullable: true),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    song_id = table.Column<long>(type: "INTEGER", nullable: true),
                    candidates = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    missing_since = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_music_video_file", x => x.id);
                    table.ForeignKey(
                        name: "fk_music_video_file_song_song_id",
                        column: x => x.song_id,
                        principalTable: "song",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_music_video_file_path",
                table: "music_video_file",
                column: "path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_music_video_file_song_id",
                table: "music_video_file",
                column: "song_id");

            migrationBuilder.CreateIndex(
                name: "ix_music_video_file_state",
                table: "music_video_file",
                column: "state");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "music_video_file");
        }
    }
}
