using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wondarr.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMusicVideoBestAvailableHeight : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "best_available_checked_at",
                table: "music_video",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "best_available_height",
                table: "music_video",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "best_available_checked_at",
                table: "music_video");

            migrationBuilder.DropColumn(
                name: "best_available_height",
                table: "music_video");
        }
    }
}
