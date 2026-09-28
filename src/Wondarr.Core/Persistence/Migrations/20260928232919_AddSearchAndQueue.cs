using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wondarr.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchAndQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "search_run",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    song_id = table.Column<long>(type: "INTEGER", nullable: false),
                    trigger = table.Column<string>(type: "TEXT", nullable: false),
                    started_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    finished_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    sources = table.Column<string>(type: "TEXT", nullable: false),
                    queries = table.Column<string>(type: "TEXT", nullable: false),
                    candidate_count = table.Column<int>(type: "INTEGER", nullable: false),
                    outcome = table.Column<string>(type: "TEXT", nullable: true),
                    message = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_search_run", x => x.id);
                    table.ForeignKey(
                        name: "fk_search_run_song_song_id",
                        column: x => x.song_id,
                        principalTable: "song",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "soulseek_user",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    username = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    successes = table.Column<int>(type: "INTEGER", nullable: false),
                    failures = table.Column<int>(type: "INTEGER", nullable: false),
                    recent_failures = table.Column<string>(type: "TEXT", nullable: false),
                    last_success_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ignored = table.Column<bool>(type: "INTEGER", nullable: false),
                    ignored_reason = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_soulseek_user", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "candidate",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    search_run_id = table.Column<long>(type: "INTEGER", nullable: false),
                    song_id = table.Column<long>(type: "INTEGER", nullable: false),
                    source_type = table.Column<string>(type: "TEXT", nullable: false),
                    source_instance_id = table.Column<long>(type: "INTEGER", nullable: true),
                    blocklist_key = table.Column<string>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", nullable: false),
                    remote_path = table.Column<string>(type: "TEXT", nullable: false),
                    provider = table.Column<string>(type: "TEXT", nullable: true),
                    quality_id = table.Column<long>(type: "INTEGER", nullable: false),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    duration_ms = table.Column<int>(type: "INTEGER", nullable: true),
                    normalised = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}"),
                    score = table.Column<int>(type: "INTEGER", nullable: false),
                    score_breakdown = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}"),
                    rejections = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    accepted = table.Column<bool>(type: "INTEGER", nullable: false),
                    grabbed = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_candidate", x => x.id);
                    table.ForeignKey(
                        name: "fk_candidate_quality_quality_id",
                        column: x => x.quality_id,
                        principalTable: "quality",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_candidate_search_run_search_run_id",
                        column: x => x.search_run_id,
                        principalTable: "search_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_candidate_song_song_id",
                        column: x => x.song_id,
                        principalTable: "song",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "queue_item",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    song_id = table.Column<long>(type: "INTEGER", nullable: false),
                    candidate_id = table.Column<long>(type: "INTEGER", nullable: false),
                    search_run_id = table.Column<long>(type: "INTEGER", nullable: false),
                    source_type = table.Column<string>(type: "TEXT", nullable: false),
                    source_instance_id = table.Column<long>(type: "INTEGER", nullable: true),
                    handle = table.Column<string>(type: "TEXT", nullable: true),
                    destination = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    progress = table.Column<double>(type: "REAL", nullable: false),
                    bytes_transferred = table.Column<long>(type: "INTEGER", nullable: false),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    place_in_queue = table.Column<int>(type: "INTEGER", nullable: true),
                    message = table.Column<string>(type: "TEXT", nullable: true),
                    download_path = table.Column<string>(type: "TEXT", nullable: true),
                    attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    state_changed_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_progress_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    next_check_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    finished_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_queue_item", x => x.id);
                    table.ForeignKey(
                        name: "fk_queue_item_candidate_candidate_id",
                        column: x => x.candidate_id,
                        principalTable: "candidate",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_queue_item_search_run_search_run_id",
                        column: x => x.search_run_id,
                        principalTable: "search_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_queue_item_song_song_id",
                        column: x => x.song_id,
                        principalTable: "song",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_candidate_quality_id",
                table: "candidate",
                column: "quality_id");

            migrationBuilder.CreateIndex(
                name: "ix_candidate_search_run_id",
                table: "candidate",
                column: "search_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_candidate_song_id",
                table: "candidate",
                column: "song_id");

            migrationBuilder.CreateIndex(
                name: "ix_candidate_song_id_blocklist_key",
                table: "candidate",
                columns: new[] { "song_id", "blocklist_key" });

            migrationBuilder.CreateIndex(
                name: "ix_queue_item_candidate_id",
                table: "queue_item",
                column: "candidate_id");

            migrationBuilder.CreateIndex(
                name: "ix_queue_item_search_run_id",
                table: "queue_item",
                column: "search_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_queue_item_song_id",
                table: "queue_item",
                column: "song_id");

            migrationBuilder.CreateIndex(
                name: "ix_queue_item_source_type_handle",
                table: "queue_item",
                columns: new[] { "source_type", "handle" });

            migrationBuilder.CreateIndex(
                name: "ix_queue_item_state",
                table: "queue_item",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ix_search_run_song_id",
                table: "search_run",
                column: "song_id");

            migrationBuilder.CreateIndex(
                name: "ix_search_run_started_at",
                table: "search_run",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_soulseek_user_username",
                table: "soulseek_user",
                column: "username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "queue_item");

            migrationBuilder.DropTable(
                name: "soulseek_user");

            migrationBuilder.DropTable(
                name: "candidate");

            migrationBuilder.DropTable(
                name: "search_run");
        }
    }
}
