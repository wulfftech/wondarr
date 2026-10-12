using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wondarr.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenImportListSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "policy_unmonitored",
                table: "import_list_item",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "last_complete_read_count",
                table: "import_list",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "suspect_read_message",
                table: "import_list",
                type: "TEXT",
                nullable: true);

            // R-020: overlapping syncs could insert one source item twice. The unique index below
            // needs one row per (list, item), so the duplicates go first. The oldest row is kept; when
            // it never got a song but a duplicate did, the kept row takes that song (and the state and
            // reason that went with it), so no added song loses the item that produced it.
            migrationBuilder.Sql(
                """
                UPDATE import_list_item
                SET song_id = (SELECT d.song_id FROM import_list_item d
                               WHERE d.import_list_id = import_list_item.import_list_id
                                 AND d.external_id = import_list_item.external_id
                                 AND d.id <> import_list_item.id AND d.song_id IS NOT NULL
                               ORDER BY d.id LIMIT 1),
                    state = 'Added',
                    reason = (SELECT d.reason FROM import_list_item d
                              WHERE d.import_list_id = import_list_item.import_list_id
                                AND d.external_id = import_list_item.external_id
                                AND d.id <> import_list_item.id AND d.song_id IS NOT NULL
                              ORDER BY d.id LIMIT 1)
                WHERE song_id IS NULL
                  AND id = (SELECT MIN(k.id) FROM import_list_item k
                            WHERE k.import_list_id = import_list_item.import_list_id
                              AND k.external_id = import_list_item.external_id)
                  AND EXISTS (SELECT 1 FROM import_list_item d
                              WHERE d.import_list_id = import_list_item.import_list_id
                                AND d.external_id = import_list_item.external_id
                                AND d.id <> import_list_item.id AND d.song_id IS NOT NULL);
                """);

            migrationBuilder.Sql(
                """
                DELETE FROM import_list_item
                WHERE id NOT IN (SELECT MIN(id) FROM import_list_item GROUP BY import_list_id, external_id);
                """);

            migrationBuilder.CreateIndex(
                name: "ix_import_list_item_import_list_id_external_id",
                table: "import_list_item",
                columns: new[] { "import_list_id", "external_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_import_list_item_import_list_id_external_id",
                table: "import_list_item");

            migrationBuilder.DropColumn(
                name: "policy_unmonitored",
                table: "import_list_item");

            migrationBuilder.DropColumn(
                name: "last_complete_read_count",
                table: "import_list");

            migrationBuilder.DropColumn(
                name: "suspect_read_message",
                table: "import_list");
        }
    }
}
