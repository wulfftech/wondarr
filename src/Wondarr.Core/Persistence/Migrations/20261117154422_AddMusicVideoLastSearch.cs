using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wondarr.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMusicVideoLastSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "music_video_last_search",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    song_id = table.Column<long>(type: "INTEGER", nullable: false),
                    searched_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_music_video_last_search", x => x.id);
                    table.ForeignKey(
                        name: "fk_music_video_last_search_song_song_id",
                        column: x => x.song_id,
                        principalTable: "song",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_music_video_last_search_song_id",
                table: "music_video_last_search",
                column: "song_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "music_video_last_search");
        }
    }
}
