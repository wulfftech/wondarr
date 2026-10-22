using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Core.Tests.Persistence;

/// <summary>
/// DECISIONS build session 14 #32: an unchanged reference file is never read again, so the migration that
/// adds the likely-YouTube-rip flag sets it from the probe the scan stored, and rates the song files such a
/// row owns (or was adopted into) no better than OPUS-160. Everything else keeps its quality.
/// </summary>
public sealed class ReferenceReplacementMigrationTests
{
    private const string PreviousMigration = "20261013151624_AddNotificationDigestEntries";

    [Fact]
    public async Task A_database_from_the_previous_version_flags_its_rips_and_rates_their_files_opus_160()
    {
        using var database = new SqliteTestDatabase();
        await using (var context = database.CreateContext(new FakeTimeProvider()))
        {
            await context.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        // Row 1 is Hip to Be Square (an MP3 at 48 kHz that measures as V0, quality 30), row 2 a CD rip at
        // 44.1 kHz, row 3 a rip that measures MP3-128 (quality 13, already below OPUS-160), row 4 a 48 kHz FLAC.
        await ExecuteAsync(
            database,
            """
            INSERT INTO reference_library (id, name, root_path, mode, enabled, created_at, updated_at)
            VALUES (1, 'Music', '/data/music', 'Reference', 1, '2026-10-01', '2026-10-01');

            INSERT INTO reference_file (id, reference_library_id, relative_path, size, modified_at, probe, confidence, state, last_seen_at, created_at, updated_at)
            VALUES
              (1, 1, 'Hip to Be Square.mp3', 1, '2026-10-01', '{"codec":"mp3","container":"mp3","bitrateKbps":268,"sampleRate":48000,"bitDepth":null,"channels":2,"durationMs":231811,"isLossless":false,"sizeBytes":7857630}', 1, 'Identified', '2026-10-01', '2026-10-01', '2026-10-01'),
              (2, 1, 'In the Air Tonight.mp3', 1, '2026-10-01', '{"codec":"mp3","container":"mp3","bitrateKbps":320,"sampleRate":44100,"bitDepth":null,"channels":2,"durationMs":335973,"isLossless":false,"sizeBytes":13400000}', 1, 'Identified', '2026-10-01', '2026-10-01', '2026-10-01'),
              (3, 1, 'L.E.S. Artistes.mp3', 1, '2026-10-01', '{"codec":"mp3","container":"mp3","bitrateKbps":128,"sampleRate":48000,"bitDepth":null,"channels":2,"durationMs":229000,"isLossless":false,"sizeBytes":3700000}', 1, 'Identified', '2026-10-01', '2026-10-01', '2026-10-01'),
              (4, 1, 'Studio.flac', 1, '2026-10-01', '{"codec":"flac","container":"flac","bitrateKbps":2300,"sampleRate":48000,"bitDepth":24,"channels":2,"durationMs":231811,"isLossless":true,"sizeBytes":66000000}', 1, 'Identified', '2026-10-01', '2026-10-01', '2026-10-01');

            INSERT INTO song_file (id, song_id, path, size, codec, container, quality_id, fingerprint_verified, source_type, source_ref, imported_at, created_at, updated_at)
            VALUES
              (1, 1, '/data/music/Hip to Be Square.mp3', 1, 'mp3', 'mp3', 30, 0, 'reference', '{"referenceLibraryId":1,"referenceFileId":1}', '2026-10-01', '2026-10-01', '2026-10-01'),
              (2, 2, '/data/music/In the Air Tonight.mp3', 1, 'mp3', 'mp3', 29, 0, 'reference', '{"referenceLibraryId":1,"referenceFileId":2}', '2026-10-01', '2026-10-01', '2026-10-01'),
              (3, 3, '/data/music/L.E.S. Artistes.mp3', 1, 'mp3', 'mp3', 13, 0, 'reference', '{"referenceLibraryId":1,"referenceFileId":3}', '2026-10-01', '2026-10-01', '2026-10-01'),
              (4, 4, '/data/music/Studio.flac', 1, 'flac', 'flac', 36, 0, 'reference', '{"referenceLibraryId":1,"referenceFileId":4}', '2026-10-01', '2026-10-01', '2026-10-01'),
              (5, 5, '/library/Hip to Be Square.mp3', 1, 'mp3', 'mp3', 30, 0, 'adopted', '{"referenceLibraryId":1,"referenceFileId":1}', '2026-10-01', '2026-10-01', '2026-10-01'),
              (6, 6, '/library/Download.mp3', 1, 'mp3', 'mp3', 30, 0, 'soulseek', '{"provider":"peer","referenceFileId":1}', '2026-10-01', '2026-10-01', '2026-10-01');
            """);

        await using (var context = database.CreateContext(new FakeTimeProvider()))
        {
            await context.GetService<IMigrator>().MigrateAsync();
        }

        (await database.ReadStringsAsync("SELECT relative_path FROM reference_file WHERE likely_youtube_rip = 1 ORDER BY id;"))
            .Should().Equal("Hip to Be Square.mp3", "L.E.S. Artistes.mp3");
        (await database.ReadStringsAsync("SELECT quality_id FROM song_file ORDER BY id;"))
            .Should().Equal(["28", "29", "13", "36", "28", "30"], "a rip and its adopted copy are rated OPUS-160; a worse rip, a CD rip, a FLAC and a download keep theirs");
        (await database.ReadStringsAsync("SELECT COUNT(*) FROM reference_file WHERE replace = 1;")).Should().Equal("0");
        (await database.ReadStringsAsync("SELECT COUNT(*) FROM reference_library WHERE replace_below_cutoff = 1;")).Should().Equal("0");
    }

    private static async Task ExecuteAsync(SqliteTestDatabase database, string sql)
    {
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={database.FilePath}"));

        await using var connection = new SqliteConnection($"Data Source={database.FilePath};Pooling=False;Foreign Keys=False");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
