using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wondarr.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReferenceReplacement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "replace_below_cutoff",
                table: "reference_library",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "likely_youtube_rip",
                table: "reference_file",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "replace",
                table: "reference_file",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // DECISIONS build session 14 #32. A file the scan read before this release is not read again
            // while it stays unchanged, so its flag comes from the probe the scan stored (camelCase JSON:
            // a lossy file at 48 kHz), and the song files it owns or was copied to are rated the way the
            // scan and the adopter now rate them: no better than OPUS-160 (quality 28).
            migrationBuilder.Sql(
                """
                UPDATE reference_file SET likely_youtube_rip = 1
                WHERE probe IS NOT NULL
                  AND json_valid(probe)
                  AND json_extract(probe, '$.isLossless') = 0
                  AND json_extract(probe, '$.sampleRate') = 48000;
                """);

            migrationBuilder.Sql(
                """
                UPDATE song_file SET quality_id = 28
                WHERE source_type IN ('reference', 'adopted')
                  AND json_valid(source_ref)
                  AND quality_id IN (SELECT id FROM quality WHERE rank > (SELECT rank FROM quality WHERE id = 28))
                  AND EXISTS (SELECT 1 FROM reference_file r
                              WHERE r.id = json_extract(song_file.source_ref, '$.referenceFileId')
                                AND r.likely_youtube_rip = 1);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "replace_below_cutoff",
                table: "reference_library");

            migrationBuilder.DropColumn(
                name: "likely_youtube_rip",
                table: "reference_file");

            migrationBuilder.DropColumn(
                name: "replace",
                table: "reference_file");
        }
    }
}
