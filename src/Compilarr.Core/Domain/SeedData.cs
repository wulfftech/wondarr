namespace Compilarr.Core.Domain;

/// <summary>
/// The rows every Compilarr install starts with: the quality ladder, the two default quality profiles
/// and the default library. Ids are a public contract — quality ids end up in <c>song_file</c> and
/// <c>history</c> rows and profile ids in <c>song</c> rows, so they are never renumbered; new
/// qualities are appended with the next free id.
/// </summary>
public static class SeedData
{
    /// <summary>The id of the seeded "Standard 320" profile.</summary>
    public const long StandardProfileId = 1;

    /// <summary>The id of the seeded "Lossless" profile.</summary>
    public const long LosslessProfileId = 2;

    /// <summary>The id of the seeded "Music" library.</summary>
    public const long DefaultLibraryId = 1;

    /// <summary>
    /// Every seeded row shares this timestamp: <c>HasData</c> needs fixed values so that regenerating
    /// the migration is a no-op.
    /// </summary>
    private static readonly DateTime SeedTimestamp = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Gets the seeded quality ladder, ordered by group rank.</summary>
    public static readonly Quality[] Qualities =
    [
        NewQuality(1, "Unknown", "Unknown", 1, "unknown", lossless: false),
        NewQuality(2, "MP3-8", "Trash lossy", 2, "mp3", lossless: false, 8, 8),
        NewQuality(3, "MP3-16", "Trash lossy", 2, "mp3", lossless: false, 16, 16),
        NewQuality(4, "MP3-24", "Trash lossy", 2, "mp3", lossless: false, 24, 24),
        NewQuality(5, "MP3-32", "Trash lossy", 2, "mp3", lossless: false, 32, 32),
        NewQuality(6, "MP3-40", "Trash lossy", 2, "mp3", lossless: false, 40, 40),
        NewQuality(7, "MP3-48", "Trash lossy", 2, "mp3", lossless: false, 48, 48),
        NewQuality(8, "MP3-56", "Trash lossy", 2, "mp3", lossless: false, 56, 56),
        NewQuality(9, "MP3-64", "Trash lossy", 2, "mp3", lossless: false, 64, 64),
        NewQuality(10, "MP3-80", "Trash lossy", 2, "mp3", lossless: false, 80, 80),
        NewQuality(11, "MP3-96", "Poor lossy", 3, "mp3", lossless: false, 96, 96),
        NewQuality(12, "MP3-112", "Poor lossy", 3, "mp3", lossless: false, 112, 112),
        NewQuality(13, "MP3-128", "Poor lossy", 3, "mp3", lossless: false, 128, 128),
        NewQuality(14, "MP3-160", "Poor lossy", 3, "mp3", lossless: false, 160, 160),
        NewQuality(15, "Vorbis Q5", "Poor lossy", 3, "vorbis", lossless: false, 140, 180),
        NewQuality(16, "OPUS-96", "Poor lossy", 3, "opus", lossless: false, 80, 112),
        NewQuality(17, "MP3-192", "Low lossy", 4, "mp3", lossless: false, 192, 192),
        NewQuality(18, "MP3-224", "Low lossy", 4, "mp3", lossless: false, 224, 224),
        NewQuality(19, "AAC-192", "Low lossy", 4, "aac", lossless: false, 192, 192),
        NewQuality(20, "Vorbis Q6", "Low lossy", 4, "vorbis", lossless: false, 180, 210),
        NewQuality(21, "WMA", "Low lossy", 4, "wma", lossless: false),
        NewQuality(22, "OPUS-128", "Low lossy", 4, "opus", lossless: false, 112, 144),
        NewQuality(23, "MP3-256", "Mid lossy", 5, "mp3", lossless: false, 256, 256),
        NewQuality(24, "MP3-VBR-V2", "Mid lossy", 5, "mp3", lossless: false, 170, 250),
        NewQuality(25, "AAC-256", "Mid lossy", 5, "aac", lossless: false, 256, 256),
        NewQuality(26, "Vorbis Q7", "Mid lossy", 5, "vorbis", lossless: false, 210, 240),
        NewQuality(27, "Vorbis Q8", "Mid lossy", 5, "vorbis", lossless: false, 240, 280),
        NewQuality(28, "OPUS-160", "Mid lossy", 5, "opus", lossless: false, 144, 176),
        NewQuality(29, "MP3-320", "High lossy", 6, "mp3", lossless: false, 320, 320),
        NewQuality(30, "MP3-VBR-V0", "High lossy", 6, "mp3", lossless: false, 220, 320),
        NewQuality(31, "AAC-320", "High lossy", 6, "aac", lossless: false, 320, 320),
        NewQuality(32, "AAC-VBR", "High lossy", 6, "aac", lossless: false),
        NewQuality(33, "Vorbis Q9", "High lossy", 6, "vorbis", lossless: false, 280, 340),
        NewQuality(34, "Vorbis Q10", "High lossy", 6, "vorbis", lossless: false, 340, 500),
        NewQuality(35, "OPUS-192+", "High lossy", 6, "opus", lossless: false, 176, 510),
        NewQuality(36, "FLAC", "Lossless", 7, "flac", lossless: true, bitDepth: 16),
        NewQuality(37, "ALAC", "Lossless", 7, "alac", lossless: true, bitDepth: 16),
        NewQuality(38, "APE", "Lossless", 7, "ape", lossless: true),
        NewQuality(39, "WavPack", "Lossless", 7, "wavpack", lossless: true),
        NewQuality(40, "FLAC 24-bit", "Hi-res lossless", 8, "flac", lossless: true, bitDepth: 24),
        NewQuality(41, "ALAC 24-bit", "Hi-res lossless", 8, "alac", lossless: true, bitDepth: 24),
        NewQuality(42, "WAV", "Uncompressed", 9, "wav", lossless: true),
        NewQuality(43, "AIFF", "Uncompressed", 9, "aiff", lossless: true),
    ];

    /// <summary>Gets the seeded quality profiles, items ordered worst → best.</summary>
    public static readonly QualityProfile[] QualityProfiles =
    [
        new()
        {
            Id = StandardProfileId,
            Name = "Standard 320",
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
            CutoffQualityId = 29,
            UpgradeAllowed = true,
            MinScore = 0,
            DurationToleranceMs = 3000,
            Items =
            [
                Item(null, false, 1),
                Item(null, false, 2, 3, 4, 5, 6, 7, 8, 9, 10),
                Item(null, false, 11, 12, 13, 14, 15, 16),
                Item("Low lossy", true, 17, 18, 19, 22),
                Item(null, false, 20, 21),
                Item("Mid lossy", true, 23, 24, 28),
                Item(null, false, 26, 27),
                // The cutoff group: AAC-256 (25) is a 256 kbps grab and counts as met.
                Item("High lossy", true, 29, 30, 25, 31, 32),
                Item(null, false, 33, 34, 35),
                Item("Lossless", true, 36, 37),
                Item(null, false, 38, 39),
                Item("Hi-res lossless", true, 40, 41),
                Item(null, false, 42, 43),
            ],
        },
        new()
        {
            Id = LosslessProfileId,
            Name = "Lossless",
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
            CutoffQualityId = 36,
            UpgradeAllowed = true,
            MinScore = 0,
            DurationToleranceMs = 3000,
            Items =
            [
                Item(null, false, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28),
                Item("High lossy", true, 29, 30, 31),
                Item(null, false, 32, 33, 34, 35),
                Item("Lossless", true, 36, 37),
                Item(null, false, 38, 39),
                Item("Hi-res lossless", true, 40, 41),
                Item(null, false, 42, 43),
            ],
        },
    ];

    /// <summary>Gets the seeded libraries.</summary>
    public static readonly Library[] Libraries =
    [
        new()
        {
            Id = DefaultLibraryId,
            Name = "Music",
            RootPath = "/data/music",
            Layout = LibraryLayout.Plexamp,
            NamingTemplate = "{Album Artist Name}/{Album Title}/{medium:0}{track:00} - {Track Title}",
            SidecarOptions = "{}",
            AlbumPolicy = AlbumPolicy.FewestAlbums,
            MinTracksPerRealAlbum = 2,
            IsDefault = true,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        },
    ];

    private static Quality NewQuality(
        long id,
        string name,
        string group,
        int rank,
        string codec,
        bool lossless,
        int? minBitrate = null,
        int? maxBitrate = null,
        int? bitDepth = null) =>
        new()
        {
            Id = id,
            Name = name,
            Group = group,
            Rank = rank,
            Codec = codec,
            Lossless = lossless,
            MinBitrate = minBitrate,
            MaxBitrate = maxBitrate,
            BitDepth = bitDepth,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };

    private static QualityProfileItem Item(string? name, bool allowed, params long[] qualityIds) =>
        new(name, [.. qualityIds], allowed);
}
