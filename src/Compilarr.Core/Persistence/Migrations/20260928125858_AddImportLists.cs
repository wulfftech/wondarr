using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Compilarr.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddImportLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "import_list",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    type = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    settings = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}"),
                    policy = table.Column<string>(type: "TEXT", nullable: false),
                    quality_profile_id = table.Column<long>(type: "INTEGER", nullable: false),
                    library_id = table.Column<long>(type: "INTEGER", nullable: false),
                    last_synced_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_list", x => x.id);
                    table.ForeignKey(
                        name: "fk_import_list_libraries_library_id",
                        column: x => x.library_id,
                        principalTable: "library",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_import_list_quality_profile_quality_profile_id",
                        column: x => x.quality_profile_id,
                        principalTable: "quality_profile",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "import_list_item",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    import_list_id = table.Column<long>(type: "INTEGER", nullable: false),
                    external_id = table.Column<string>(type: "TEXT", nullable: false),
                    song_id = table.Column<long>(type: "INTEGER", nullable: true),
                    raw = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}"),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: true),
                    candidates = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_list_item", x => x.id);
                    table.ForeignKey(
                        name: "fk_import_list_item_import_list_import_list_id",
                        column: x => x.import_list_id,
                        principalTable: "import_list",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_import_list_item_song_song_id",
                        column: x => x.song_id,
                        principalTable: "song",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_import_list_library_id",
                table: "import_list",
                column: "library_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_list_quality_profile_id",
                table: "import_list",
                column: "quality_profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_list_item_import_list_id",
                table: "import_list_item",
                column: "import_list_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_list_item_song_id",
                table: "import_list_item",
                column: "song_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_list_item_state",
                table: "import_list_item",
                column: "state");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "import_list_item");

            migrationBuilder.DropTable(
                name: "import_list");
        }
    }
}
