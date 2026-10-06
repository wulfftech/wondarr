using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wondarr.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryOutputPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "output_policy",
                table: "library",
                type: "TEXT",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "library",
                keyColumn: "id",
                keyValue: 1L,
                column: "output_policy",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "output_policy",
                table: "library");
        }
    }
}
