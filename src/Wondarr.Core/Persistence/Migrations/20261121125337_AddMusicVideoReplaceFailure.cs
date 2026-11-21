using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wondarr.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMusicVideoReplaceFailure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "replace_failed_at",
                table: "music_video",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "replace_failure_reason",
                table: "music_video",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "replace_failed_at",
                table: "music_video");

            migrationBuilder.DropColumn(
                name: "replace_failure_reason",
                table: "music_video");
        }
    }
}
