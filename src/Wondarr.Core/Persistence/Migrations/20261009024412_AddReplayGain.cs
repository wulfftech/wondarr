using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wondarr.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReplayGain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "replay_gain_db",
                table: "song_file",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "replay_gain_peak",
                table: "song_file",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "replay_gain",
                table: "library",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "library",
                keyColumn: "id",
                keyValue: 1L,
                column: "replay_gain",
                value: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "replay_gain_db",
                table: "song_file");

            migrationBuilder.DropColumn(
                name: "replay_gain_peak",
                table: "song_file");

            migrationBuilder.DropColumn(
                name: "replay_gain",
                table: "library");
        }
    }
}
