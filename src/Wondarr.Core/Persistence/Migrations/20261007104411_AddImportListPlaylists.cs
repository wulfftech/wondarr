using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wondarr.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddImportListPlaylists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "plex_rating_key",
                table: "import_list_item",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "plex_rating_key_path",
                table: "import_list_item",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "m3u_export",
                table: "import_list",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "plex_playlist",
                table: "import_list",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "plex_playlist_key",
                table: "import_list",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "plex_rating_key",
                table: "import_list_item");

            migrationBuilder.DropColumn(
                name: "plex_rating_key_path",
                table: "import_list_item");

            migrationBuilder.DropColumn(
                name: "m3u_export",
                table: "import_list");

            migrationBuilder.DropColumn(
                name: "plex_playlist",
                table: "import_list");

            migrationBuilder.DropColumn(
                name: "plex_playlist_key",
                table: "import_list");
        }
    }
}
