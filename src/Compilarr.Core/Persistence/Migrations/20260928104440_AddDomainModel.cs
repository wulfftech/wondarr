using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Compilarr.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDomainModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "artist",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    sort_name = table.Column<string>(type: "TEXT", nullable: false),
                    mb_artist_id = table.Column<string>(type: "TEXT", nullable: true),
                    spotify_id = table.Column<string>(type: "TEXT", nullable: true),
                    deezer_id = table.Column<long>(type: "INTEGER", nullable: true),
                    monitored_top_n = table.Column<int>(type: "INTEGER", nullable: true),
                    tags = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_artist", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "library",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    root_path = table.Column<string>(type: "TEXT", nullable: false),
                    layout = table.Column<string>(type: "TEXT", nullable: false),
                    naming_template = table.Column<string>(type: "TEXT", nullable: false),
                    sidecar_options = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}"),
                    album_policy = table.Column<string>(type: "TEXT", nullable: false),
                    min_tracks_per_real_album = table.Column<int>(type: "INTEGER", nullable: false),
                    plex_section_id = table.Column<string>(type: "TEXT", nullable: true),
                    is_default = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_library", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "quality",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    group = table.Column<string>(type: "TEXT", nullable: false),
                    rank = table.Column<int>(type: "INTEGER", nullable: false),
                    codec = table.Column<string>(type: "TEXT", nullable: false),
                    lossless = table.Column<bool>(type: "INTEGER", nullable: false),
                    min_bitrate = table.Column<int>(type: "INTEGER", nullable: true),
                    max_bitrate = table.Column<int>(type: "INTEGER", nullable: true),
                    bit_depth = table.Column<int>(type: "INTEGER", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quality", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "quality_profile",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    items = table.Column<string>(type: "TEXT", nullable: false),
                    cutoff_quality_id = table.Column<long>(type: "INTEGER", nullable: false),
                    upgrade_allowed = table.Column<bool>(type: "INTEGER", nullable: false),
                    min_score = table.Column<int>(type: "INTEGER", nullable: false),
                    duration_tolerance_ms = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quality_profile", x => x.id);
                    table.ForeignKey(
                        name: "fk_quality_profile_quality_cutoff_quality_id",
                        column: x => x.cutoff_quality_id,
                        principalTable: "quality",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "song",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    artist_credit = table.Column<string>(type: "TEXT", nullable: false),
                    primary_artist_id = table.Column<long>(type: "INTEGER", nullable: false),
                    mb_recording_id = table.Column<string>(type: "TEXT", nullable: true),
                    mb_work_id = table.Column<string>(type: "TEXT", nullable: true),
                    isrcs = table.Column<string>(type: "TEXT", nullable: false),
                    spotify_id = table.Column<string>(type: "TEXT", nullable: true),
                    deezer_id = table.Column<long>(type: "INTEGER", nullable: true),
                    ytm_video_id = table.Column<string>(type: "TEXT", nullable: true),
                    duration_ms = table.Column<int>(type: "INTEGER", nullable: true),
                    version_flags = table.Column<string>(type: "TEXT", nullable: false),
                    monitored = table.Column<bool>(type: "INTEGER", nullable: false),
                    quality_profile_id = table.Column<long>(type: "INTEGER", nullable: false),
                    source_profile_id = table.Column<long>(type: "INTEGER", nullable: true),
                    library_id = table.Column<long>(type: "INTEGER", nullable: false),
                    added_by = table.Column<string>(type: "TEXT", nullable: false),
                    tags = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_song", x => x.id);
                    table.ForeignKey(
                        name: "fk_song_artist_primary_artist_id",
                        column: x => x.primary_artist_id,
                        principalTable: "artist",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_song_libraries_library_id",
                        column: x => x.library_id,
                        principalTable: "library",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_song_quality_profiles_quality_profile_id",
                        column: x => x.quality_profile_id,
                        principalTable: "quality_profile",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "album_context",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    song_id = table.Column<long>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    album_title = table.Column<string>(type: "TEXT", nullable: false),
                    album_artist = table.Column<string>(type: "TEXT", nullable: false),
                    album_key = table.Column<string>(type: "TEXT", nullable: false),
                    mb_release_id = table.Column<string>(type: "TEXT", nullable: true),
                    mb_release_group_id = table.Column<string>(type: "TEXT", nullable: true),
                    track_no = table.Column<int>(type: "INTEGER", nullable: true),
                    disc_no = table.Column<int>(type: "INTEGER", nullable: true),
                    total_tracks = table.Column<int>(type: "INTEGER", nullable: true),
                    date = table.Column<string>(type: "TEXT", nullable: true),
                    original_date = table.Column<string>(type: "TEXT", nullable: true),
                    label = table.Column<string>(type: "TEXT", nullable: true),
                    cover_url = table.Column<string>(type: "TEXT", nullable: true),
                    is_various_artists = table.Column<bool>(type: "INTEGER", nullable: false),
                    sticky = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_album_context", x => x.id);
                    table.ForeignKey(
                        name: "fk_album_context_song_song_id",
                        column: x => x.song_id,
                        principalTable: "song",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "song_artist",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    song_id = table.Column<long>(type: "INTEGER", nullable: false),
                    artist_id = table.Column<long>(type: "INTEGER", nullable: false),
                    role = table.Column<string>(type: "TEXT", nullable: false),
                    position = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_song_artist", x => x.id);
                    table.ForeignKey(
                        name: "fk_song_artist_artist_artist_id",
                        column: x => x.artist_id,
                        principalTable: "artist",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_song_artist_song_song_id",
                        column: x => x.song_id,
                        principalTable: "song",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "song_file",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    song_id = table.Column<long>(type: "INTEGER", nullable: false),
                    path = table.Column<string>(type: "TEXT", nullable: false),
                    size = table.Column<long>(type: "INTEGER", nullable: false),
                    codec = table.Column<string>(type: "TEXT", nullable: false),
                    container = table.Column<string>(type: "TEXT", nullable: false),
                    bitrate_kbps = table.Column<int>(type: "INTEGER", nullable: true),
                    sample_rate = table.Column<int>(type: "INTEGER", nullable: true),
                    bit_depth = table.Column<int>(type: "INTEGER", nullable: true),
                    channels = table.Column<int>(type: "INTEGER", nullable: true),
                    duration_ms = table.Column<int>(type: "INTEGER", nullable: true),
                    quality_id = table.Column<long>(type: "INTEGER", nullable: false),
                    acoust_id = table.Column<string>(type: "TEXT", nullable: true),
                    fingerprint_verified = table.Column<bool>(type: "INTEGER", nullable: false),
                    source_type = table.Column<string>(type: "TEXT", nullable: false),
                    source_ref = table.Column<string>(type: "TEXT", nullable: true),
                    imported_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    tags_written = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_song_file", x => x.id);
                    table.ForeignKey(
                        name: "fk_song_file_qualities_quality_id",
                        column: x => x.quality_id,
                        principalTable: "quality",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_song_file_song_song_id",
                        column: x => x.song_id,
                        principalTable: "song",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "library",
                columns: new[] { "id", "album_policy", "created_at", "is_default", "layout", "min_tracks_per_real_album", "name", "naming_template", "plex_section_id", "root_path", "sidecar_options", "updated_at" },
                values: new object[] { 1L, "FewestAlbums", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), true, "Plexamp", 2, "Music", "{Album Artist Name}/{Album Title}/{medium:0}{track:00} - {Track Title}", null, "/data/music", "{}", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) });

            migrationBuilder.InsertData(
                table: "quality",
                columns: new[] { "id", "bit_depth", "codec", "created_at", "group", "lossless", "max_bitrate", "min_bitrate", "name", "rank", "updated_at" },
                values: new object[,]
                {
                    { 1L, null, "unknown", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Unknown", false, null, null, "Unknown", 1, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 2L, null, "mp3", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Trash lossy", false, 8, 8, "MP3-8", 2, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 3L, null, "mp3", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Trash lossy", false, 16, 16, "MP3-16", 2, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 4L, null, "mp3", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Trash lossy", false, 24, 24, "MP3-24", 2, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 5L, null, "mp3", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Trash lossy", false, 32, 32, "MP3-32", 2, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 6L, null, "mp3", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Trash lossy", false, 40, 40, "MP3-40", 2, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 7L, null, "mp3", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Trash lossy", false, 48, 48, "MP3-48", 2, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 8L, null, "mp3", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Trash lossy", false, 56, 56, "MP3-56", 2, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 9L, null, "mp3", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Trash lossy", false, 64, 64, "MP3-64", 2, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 10L, null, "mp3", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Trash lossy", false, 80, 80, "MP3-80", 2, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 11L, null, "mp3", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Poor lossy", false, 96, 96, "MP3-96", 3, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 12L, null, "mp3", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Poor lossy", false, 112, 112, "MP3-112", 3, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 13L, null, "mp3", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Poor lossy", false, 128, 128, "MP3-128", 3, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 14L, null, "mp3", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Poor lossy", false, 160, 160, "MP3-160", 3, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 15L, null, "vorbis", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Poor lossy", false, 180, 140, "Vorbis Q5", 3, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 16L, null, "opus", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Poor lossy", false, 112, 80, "OPUS-96", 3, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 17L, null, "mp3", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Low lossy", false, 192, 192, "MP3-192", 4, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 18L, null, "mp3", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Low lossy", false, 224, 224, "MP3-224", 4, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 19L, null, "aac", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Low lossy", false, 192, 192, "AAC-192", 4, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 20L, null, "vorbis", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Low lossy", false, 210, 180, "Vorbis Q6", 4, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 21L, null, "wma", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Low lossy", false, null, null, "WMA", 4, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 22L, null, "opus", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Low lossy", false, 144, 112, "OPUS-128", 4, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 23L, null, "mp3", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Mid lossy", false, 256, 256, "MP3-256", 5, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 24L, null, "mp3", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Mid lossy", false, 250, 170, "MP3-VBR-V2", 5, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 25L, null, "aac", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Mid lossy", false, 256, 256, "AAC-256", 5, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 26L, null, "vorbis", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Mid lossy", false, 240, 210, "Vorbis Q7", 5, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 27L, null, "vorbis", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Mid lossy", false, 280, 240, "Vorbis Q8", 5, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 28L, null, "opus", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Mid lossy", false, 176, 144, "OPUS-160", 5, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 29L, null, "mp3", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "High lossy", false, 320, 320, "MP3-320", 6, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 30L, null, "mp3", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "High lossy", false, 320, 220, "MP3-VBR-V0", 6, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 31L, null, "aac", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "High lossy", false, 320, 320, "AAC-320", 6, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 32L, null, "aac", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "High lossy", false, null, null, "AAC-VBR", 6, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 33L, null, "vorbis", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "High lossy", false, 340, 280, "Vorbis Q9", 6, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 34L, null, "vorbis", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "High lossy", false, 500, 340, "Vorbis Q10", 6, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 35L, null, "opus", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "High lossy", false, 510, 176, "OPUS-192+", 6, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 36L, 16, "flac", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Lossless", true, null, null, "FLAC", 7, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 37L, 16, "alac", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Lossless", true, null, null, "ALAC", 7, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 38L, null, "ape", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Lossless", true, null, null, "APE", 7, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 39L, null, "wavpack", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Lossless", true, null, null, "WavPack", 7, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 40L, 24, "flac", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Hi-res lossless", true, null, null, "FLAC 24-bit", 8, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 41L, 24, "alac", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Hi-res lossless", true, null, null, "ALAC 24-bit", 8, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 42L, null, "wav", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Uncompressed", true, null, null, "WAV", 9, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 43L, null, "aiff", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Uncompressed", true, null, null, "AIFF", 9, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.InsertData(
                table: "quality_profile",
                columns: new[] { "id", "created_at", "cutoff_quality_id", "duration_tolerance_ms", "items", "min_score", "name", "updated_at", "upgrade_allowed" },
                values: new object[,]
                {
                    { 1L, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), 29L, 3000, "[{\"name\":null,\"qualityIds\":[1],\"allowed\":false},{\"name\":null,\"qualityIds\":[2,3,4,5,6,7,8,9,10],\"allowed\":false},{\"name\":null,\"qualityIds\":[11,12,13,14,15,16],\"allowed\":false},{\"name\":\"Low lossy\",\"qualityIds\":[17,18,19,22],\"allowed\":true},{\"name\":null,\"qualityIds\":[20,21],\"allowed\":false},{\"name\":\"Mid lossy\",\"qualityIds\":[23,24,28],\"allowed\":true},{\"name\":null,\"qualityIds\":[26,27],\"allowed\":false},{\"name\":\"High lossy\",\"qualityIds\":[29,30,25,31,32],\"allowed\":true},{\"name\":null,\"qualityIds\":[33,34,35],\"allowed\":false},{\"name\":\"Lossless\",\"qualityIds\":[36,37],\"allowed\":true},{\"name\":null,\"qualityIds\":[38,39],\"allowed\":false},{\"name\":\"Hi-res lossless\",\"qualityIds\":[40,41],\"allowed\":true},{\"name\":null,\"qualityIds\":[42,43],\"allowed\":false}]", 0, "Standard 320", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), true },
                    { 2L, new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), 36L, 3000, "[{\"name\":null,\"qualityIds\":[1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28],\"allowed\":false},{\"name\":\"High lossy\",\"qualityIds\":[29,30,31],\"allowed\":true},{\"name\":null,\"qualityIds\":[32,33,34,35],\"allowed\":false},{\"name\":\"Lossless\",\"qualityIds\":[36,37],\"allowed\":true},{\"name\":null,\"qualityIds\":[38,39],\"allowed\":false},{\"name\":\"Hi-res lossless\",\"qualityIds\":[40,41],\"allowed\":true},{\"name\":null,\"qualityIds\":[42,43],\"allowed\":false}]", 0, "Lossless", new DateTime(2026, 9, 28, 0, 0, 0, 0, DateTimeKind.Utc), true }
                });

            migrationBuilder.CreateIndex(
                name: "ix_album_context_album_key",
                table: "album_context",
                column: "album_key");

            migrationBuilder.CreateIndex(
                name: "ix_album_context_song_id",
                table: "album_context",
                column: "song_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_artist_mb_artist_id",
                table: "artist",
                column: "mb_artist_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_library_name",
                table: "library",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quality_name",
                table: "quality",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quality_profile_cutoff_quality_id",
                table: "quality_profile",
                column: "cutoff_quality_id");

            migrationBuilder.CreateIndex(
                name: "ix_quality_profile_name",
                table: "quality_profile",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_song_deezer_id",
                table: "song",
                column: "deezer_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_song_library_id",
                table: "song",
                column: "library_id");

            migrationBuilder.CreateIndex(
                name: "ix_song_mb_recording_id",
                table: "song",
                column: "mb_recording_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_song_primary_artist_id",
                table: "song",
                column: "primary_artist_id");

            migrationBuilder.CreateIndex(
                name: "ix_song_quality_profile_id",
                table: "song",
                column: "quality_profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_song_artist_artist_id",
                table: "song_artist",
                column: "artist_id");

            migrationBuilder.CreateIndex(
                name: "ix_song_artist_song_id_position",
                table: "song_artist",
                columns: new[] { "song_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_song_file_quality_id",
                table: "song_file",
                column: "quality_id");

            migrationBuilder.CreateIndex(
                name: "ix_song_file_song_id",
                table: "song_file",
                column: "song_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "album_context");

            migrationBuilder.DropTable(
                name: "song_artist");

            migrationBuilder.DropTable(
                name: "song_file");

            migrationBuilder.DropTable(
                name: "song");

            migrationBuilder.DropTable(
                name: "artist");

            migrationBuilder.DropTable(
                name: "library");

            migrationBuilder.DropTable(
                name: "quality_profile");

            migrationBuilder.DropTable(
                name: "quality");
        }
    }
}
