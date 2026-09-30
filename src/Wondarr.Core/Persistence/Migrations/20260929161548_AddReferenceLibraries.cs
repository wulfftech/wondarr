using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wondarr.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReferenceLibraries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reference_library",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    root_path = table.Column<string>(type: "TEXT", nullable: false),
                    mode = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "Reference"),
                    library_id = table.Column<long>(type: "INTEGER", nullable: true),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    last_scanned_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_scan_message = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reference_library", x => x.id);
                    table.ForeignKey(
                        name: "fk_reference_library_library_library_id",
                        column: x => x.library_id,
                        principalTable: "library",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reference_file",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    reference_library_id = table.Column<long>(type: "INTEGER", nullable: false),
                    relative_path = table.Column<string>(type: "TEXT", nullable: false),
                    size = table.Column<long>(type: "INTEGER", nullable: false),
                    modified_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    probe = table.Column<string>(type: "TEXT", nullable: true),
                    tags = table.Column<string>(type: "TEXT", nullable: true),
                    fingerprint = table.Column<string>(type: "TEXT", nullable: true),
                    acoust_id = table.Column<string>(type: "TEXT", nullable: true),
                    song_id = table.Column<long>(type: "INTEGER", nullable: true),
                    confidence = table.Column<double>(type: "REAL", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    identified_by = table.Column<string>(type: "TEXT", nullable: true),
                    message = table.Column<string>(type: "TEXT", nullable: true),
                    last_seen_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    missing_since = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reference_file", x => x.id);
                    table.ForeignKey(
                        name: "fk_reference_file_reference_library_reference_library_id",
                        column: x => x.reference_library_id,
                        principalTable: "reference_library",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_reference_file_song_song_id",
                        column: x => x.song_id,
                        principalTable: "song",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "match_candidate",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    reference_file_id = table.Column<long>(type: "INTEGER", nullable: false),
                    rank = table.Column<int>(type: "INTEGER", nullable: false),
                    identity = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}"),
                    score = table.Column<double>(type: "REAL", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_match_candidate", x => x.id);
                    table.ForeignKey(
                        name: "fk_match_candidate_reference_file_reference_file_id",
                        column: x => x.reference_file_id,
                        principalTable: "reference_file",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_match_candidate_reference_file_id",
                table: "match_candidate",
                column: "reference_file_id");

            migrationBuilder.CreateIndex(
                name: "ix_reference_file_reference_library_id_relative_path",
                table: "reference_file",
                columns: new[] { "reference_library_id", "relative_path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reference_file_song_id",
                table: "reference_file",
                column: "song_id");

            migrationBuilder.CreateIndex(
                name: "ix_reference_file_state",
                table: "reference_file",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ix_reference_library_library_id",
                table: "reference_library",
                column: "library_id");

            migrationBuilder.CreateIndex(
                name: "ix_reference_library_name",
                table: "reference_library",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "match_candidate");

            migrationBuilder.DropTable(
                name: "reference_file");

            migrationBuilder.DropTable(
                name: "reference_library");
        }
    }
}
