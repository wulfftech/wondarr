using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wondarr.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryPlexPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "plex_library_path",
                table: "library",
                type: "TEXT",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "library",
                keyColumn: "id",
                keyValue: 1L,
                column: "plex_library_path",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "plex_library_path",
                table: "library");
        }
    }
}
