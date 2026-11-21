using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wondarr.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMusicVideoTitleAndFileCodecs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "audio_codec",
                table: "music_video_file",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "video_codec",
                table: "music_video_file",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "video_title",
                table: "music_video",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "audio_codec",
                table: "music_video_file");

            migrationBuilder.DropColumn(
                name: "video_codec",
                table: "music_video_file");

            migrationBuilder.DropColumn(
                name: "video_title",
                table: "music_video");
        }
    }
}
