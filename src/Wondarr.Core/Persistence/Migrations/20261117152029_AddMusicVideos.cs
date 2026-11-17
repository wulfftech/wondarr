using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wondarr.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMusicVideos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "music_video",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    song_id = table.Column<long>(type: "INTEGER", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    you_tube_video_id = table.Column<string>(type: "TEXT", nullable: false),
                    found_by = table.Column<string>(type: "TEXT", nullable: false),
                    path = table.Column<string>(type: "TEXT", nullable: true),
                    width = table.Column<int>(type: "INTEGER", nullable: true),
                    height = table.Column<int>(type: "INTEGER", nullable: true),
                    video_codec = table.Column<string>(type: "TEXT", nullable: true),
                    audio_codec = table.Column<string>(type: "TEXT", nullable: true),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    duration_ms = table.Column<int>(type: "INTEGER", nullable: true),
                    failure_reason = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_music_video", x => x.id);
                    table.ForeignKey(
                        name: "fk_music_video_song_song_id",
                        column: x => x.song_id,
                        principalTable: "song",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "music_video_blocklist",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    song_id = table.Column<long>(type: "INTEGER", nullable: false),
                    you_tube_video_id = table.Column<string>(type: "TEXT", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_music_video_blocklist", x => x.id);
                    table.ForeignKey(
                        name: "fk_music_video_blocklist_songs_song_id",
                        column: x => x.song_id,
                        principalTable: "song",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_music_video_song_id",
                table: "music_video",
                column: "song_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_music_video_blocklist_song_id_you_tube_video_id",
                table: "music_video_blocklist",
                columns: new[] { "song_id", "you_tube_video_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "music_video");

            migrationBuilder.DropTable(
                name: "music_video_blocklist");
        }
    }
}
