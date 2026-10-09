using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ATL;
using ATL.AudioData;
using Microsoft.Extensions.Logging;

namespace Wondarr.Core.Tagging;

/// <summary>
/// Writes the Plex-safe tag set of LIBRARY_OUTPUT §7.5 into an audio file.
/// </summary>
public interface ITagWriter
{
    /// <summary>
    /// Replaces every tag of the file at <paramref name="path"/> with <paramref name="tags"/>.
    /// </summary>
    /// <param name="path">Absolute path of the audio file to tag.</param>
    /// <param name="tags">The tags to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome; a failure never throws and never modifies <paramref name="path"/>.</returns>
    Task<TagWriteResult> WriteAsync(string path, TagSet tags, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets only the ReplayGain track gain and peak of the file at <paramref name="path"/>, leaving
    /// every other tag, the cover and the lyrics as they are. Written through the same temporary copy
    /// and verified read-back as <see cref="WriteAsync"/>.
    /// </summary>
    /// <param name="path">Absolute path of the audio file to tag.</param>
    /// <param name="gainDb">The track gain in dB.</param>
    /// <param name="peak">The true peak as a linear value.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome; a failure never throws and never modifies <paramref name="path"/>.</returns>
    Task<TagWriteResult> WriteReplayGainAsync(
        string path,
        double gainDb,
        double peak,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The outcome of a tag write.
/// </summary>
/// <param name="Success">True when the file was tagged, verified and swapped in.</param>
/// <param name="Error">A short failure reason; <see langword="null"/> on success.</param>
/// <param name="Written">
/// The logical field names mapped to the values read back from the file, for <c>song_file.tags_written</c>.
/// </param>
public sealed record TagWriteResult(bool Success, string? Error, IReadOnlyDictionary<string, string> Written);

/// <summary>
/// Tags a file safely: the tags are written to a temporary copy beside it, read back and compared,
/// and only then atomically swapped in, so a crash or a failed write never leaves a half-tagged file.
/// </summary>
public sealed partial class TagWriter(ILogger<TagWriter> logger) : ITagWriter
{
    private const string TempPrefix = ".wondarr-tag-";

    private static readonly IReadOnlyDictionary<string, string> EmptyWritten =
        new Dictionary<string, string>(StringComparer.Ordinal);

    static TagWriter()
    {
        // ATL settings are global (and therefore process-wide): write ID3v2.4, whose text frames are UTF-8.
        Settings.ID3v2_tagSubVersion = 4;
    }

    /// <inheritdoc />
    public async Task<TagWriteResult> WriteAsync(
        string path,
        TagSet tags,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(tags);

        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(directory))
        {
            return Failure("the file has no parent directory to hold the temporary copy");
        }

        // The copy lives beside the file on purpose: File.Move is only atomic within one volume.
        var tempPath = Path.Combine(
            directory,
            TempPrefix + Guid.NewGuid().ToString("N") + Path.GetExtension(path));

        try
        {
            File.Copy(path, tempPath, overwrite: false);

            ReplaceTags(new Track(tempPath), tags);

            var written = ReadBack(tempPath, tags);

            File.Move(tempPath, path, overwrite: true);

            LogTagged(written.Count);
            return new TagWriteResult(true, null, written);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailed(ex);
            return Failure($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            DeleteTemp(tempPath);
        }
    }

    /// <inheritdoc />
    public async Task<TagWriteResult> WriteReplayGainAsync(
        string path,
        double gainDb,
        double peak,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        cancellationToken.ThrowIfCancellationRequested();

        var gain = FormatGain(gainDb);
        var peakText = FormatPeak(peak);

        if (gain is null || peakText is null)
        {
            return Failure("the ReplayGain values are not finite numbers");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(directory))
        {
            return Failure("the file has no parent directory to hold the temporary copy");
        }

        var tempPath = Path.Combine(
            directory,
            TempPrefix + Guid.NewGuid().ToString("N") + Path.GetExtension(path));

        try
        {
            File.Copy(path, tempPath, overwrite: false);

            var keys = KeysFor(tempPath);
            var track = new Track(tempPath);

            if (track.DurationMs <= 0)
            {
                throw new InvalidOperationException("The file is not readable audio.");
            }

            // Save() rewrites the tag it loaded, so everything else the file carries stays as it is.
            track.AdditionalFields[keys.ReplayGainTrackGain] = gain;
            track.AdditionalFields[keys.ReplayGainTrackPeak] = peakText;

            if (!track.Save())
            {
                throw new InvalidOperationException("ATL refused to save the tag.");
            }

            var reread = new Track(tempPath);
            var written = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ReplayGainTrackGain"] = gain,
                ["ReplayGainTrackPeak"] = peakText,
            };

            if (GetAdditional(reread, keys.ReplayGainTrackGain) != gain
                || GetAdditional(reread, keys.ReplayGainTrackPeak) != peakText)
            {
                throw new InvalidOperationException("Tag read-back verification failed for: ReplayGain.");
            }

            File.Move(tempPath, path, overwrite: true);

            LogTagged(written.Count);
            return new TagWriteResult(true, null, written);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailed(ex);
            return Failure($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            DeleteTemp(tempPath);
        }
    }

    /// <summary>
    /// Reads every field back from the tagged copy and compares it with what was asked for; throws when
    /// a field did not land, so the caller can fall back to the untouched original.
    /// </summary>
    private static Dictionary<string, string> ReadBack(string tempPath, TagSet tags)
    {
        var keys = KeysFor(tempPath);
        var expected = ExpectedFields(tags, keys);
        var actual = ReadFields(tempPath, keys);

        var mismatched = expected
            .Where(pair => !actual.TryGetValue(pair.Key, out var value) || !SameValue(pair.Key, pair.Value, value))
            .Select(pair => pair.Key)
            .ToList();

        if (mismatched.Count > 0)
        {
            throw new InvalidOperationException(
                "Tag read-back verification failed for: " + string.Join(", ", mismatched) + ".");
        }

        return expected;
    }

    private static void ReplaceTags(Track track, TagSet tags)
    {
        // Replaces rather than merges (§7.5): anything the source carried - another album's MBIDs,
        // ReplayGain, an old cover - must not survive into the library copy. ENCODER/ENCODED_BY is
        // the documented exception, so it is read before the strip and written back after.
        if (track.DurationMs <= 0)
        {
            // ATL tags any byte sequence it is handed, so an unreadable file must be caught here:
            // tagging it would "succeed" and hand the library a file no player can use.
            throw new InvalidOperationException("The file is not readable audio.");
        }

        var encoder = track.Encoder;
        var encodedBy = track.EncodedBy;

        foreach (var tagType in StrippableTagTypes)
        {
            // False just means the file carried no tag of that system; a failed removal throws.
            track.Remove(tagType);
        }

        var keys = KeysFor(path: track.Path);

        if (Has(tags.Title))
        {
            track.Title = tags.Title;
        }

        if (Has(tags.Artist))
        {
            track.Artist = tags.Artist;
        }

        if (Has(tags.AlbumArtist))
        {
            track.AlbumArtist = tags.AlbumArtist;
        }

        if (Has(tags.Album))
        {
            track.Album = tags.Album;
        }

        if (tags.TrackNumber is { } trackNumber)
        {
            track.TrackNumber = trackNumber;
        }

        if (tags.TrackTotal is { } trackTotal)
        {
            track.TrackTotal = trackTotal;
        }

        if (tags.DiscNumber is { } discNumber)
        {
            track.DiscNumber = discNumber;
        }

        if (tags.DiscTotal is { } discTotal)
        {
            track.DiscTotal = discTotal;
        }

        if (TryParseDate(tags.Date, out var date))
        {
            // A year-only date (every pseudo-album has one) goes in as the year, so the file says
            // "1991" rather than a made-up "1991-01-01".
            if (tags.Date!.Trim().Length == 4)
            {
                track.Year = date.Year;
            }
            else
            {
                track.Date = date;
            }
        }

        if (TryParseDate(tags.OriginalDate, out var originalDate))
        {
            if (keys.OriginalDate is { } originalDateKey)
            {
                // Written as the literal string: ATL's date property would also normalize the
                // precision (and spells the Vorbis field with a trailing space).
                track.AdditionalFields[originalDateKey] = tags.OriginalDate;
            }
            else if (keys.OriginalDateViaProperty)
            {
                track.OriginalReleaseDate = originalDate;
            }
        }

        if (Has(tags.Isrc))
        {
            track.ISRC = tags.Isrc;
        }

        if (Has(tags.Genre))
        {
            track.Genre = tags.Genre;
        }

        if (Has(tags.Comment))
        {
            track.Comment = tags.Comment;
        }

        if (NormalizedLyrics(tags.Lyrics) is { } lyrics)
        {
            track.Lyrics.Clear();
            track.Lyrics.Add(new LyricsInfo
            {
                ContentType = LyricsInfo.LyricsType.LYRICS,
                Format = LyricsInfo.LyricsFormat.UNSYNCHRONIZED,
                UnsynchronizedLyrics = lyrics,
            });
        }

        if (Has(encoder))
        {
            track.Encoder = encoder;
        }

        if (Has(encodedBy))
        {
            track.EncodedBy = encodedBy;
        }

        if (tags.Artists.Count > 0)
        {
            track.AdditionalFields[keys.Artists] = JoinArtists(tags.Artists);
        }

        // ATL 7.17 has no UFID support: an "UFID:http://musicbrainz.org" key becomes a TXXX frame whose
// description is that literal string, which is worse than not writing UFID at all. The recording id
// therefore lands in the TXXX frame Picard reads (see the done-report of task P2-06).
        WriteAdditional(track, keys.RecordingId, tags.MbRecordingId);
        WriteAdditional(track, keys.ReleaseTrackId, tags.MbReleaseTrackId);
        WriteAdditional(track, keys.ReleaseId, tags.MbReleaseId);
        WriteAdditional(track, keys.ReleaseGroupId, tags.MbReleaseGroupId);
        WriteAdditional(track, keys.ArtistId, tags.MbArtistId);
        WriteAdditional(track, keys.AlbumArtistId, tags.MbAlbumArtistId);
        WriteAdditional(track, keys.ReleaseType, tags.ReleaseType);
        WriteAdditional(track, keys.ReleaseStatus, tags.ReleaseStatus);
        WriteAdditional(track, keys.AcoustId, tags.AcoustId);

        if (tags.Compilation)
        {
            track.AdditionalFields[keys.Compilation] = keys.CompilationValue;
        }

        WriteAdditional(track, keys.ReplayGainTrackGain, FormatGain(tags.ReplayGainTrackGainDb));
        WriteAdditional(track, keys.ReplayGainTrackPeak, FormatPeak(tags.ReplayGainTrackPeak));

        if (tags.FrontCover is { Length: > 0 } cover)
        {
            track.EmbeddedPictures.Clear();
            track.EmbeddedPictures.Add(PictureInfo.fromBinaryData(
                cover,
                PictureInfo.PIC_TYPE.Front,
                keys.CoverTagType,
                keys.CoverNativeCode,
                1));
        }

        if (!track.Save())
        {
            throw new InvalidOperationException("ATL refused to save the tag.");
        }
    }

    /// <summary>
    /// Every tag system a source file can carry. <c>NATIVE</c> is the format's own system (Vorbis
    /// comments in FLAC/Opus, the <c>ilst</c> atom in M4A), the others exist on MP3.
    /// </summary>
    private static readonly MetaDataIOFactory.TagType[] StrippableTagTypes =
    [
        MetaDataIOFactory.TagType.ID3V1,
        MetaDataIOFactory.TagType.ID3V2,
        MetaDataIOFactory.TagType.APE,
        MetaDataIOFactory.TagType.NATIVE,
    ];

    private static Dictionary<string, string> ReadFields(string tempPath, FormatKeys keys)
    {
        var track = new Track(tempPath);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        AddString(values, "Title", track.Title);
        AddString(values, "Artist", track.Artist);
        AddString(values, "AlbumArtist", track.AlbumArtist);
        AddString(values, "Album", track.Album);
        AddString(values, "Date", FormatDate(track.Date) ?? FormatYear(track.Year));
        AddString(values, "Isrc", track.ISRC);
        AddString(values, "Genre", track.Genre);
        AddString(values, "Comment", track.Comment);
        AddString(values, "Lyrics", UnsynchronizedLyrics(track));
        if (keys.OriginalDate is { } originalDateKey)
        {
            // ATL writes the literal field but maps it on reading to its own (misspelled) key, so it
            // shows up nowhere; the Vorbis comment is read straight from the file instead.
            AddString(values, "OriginalDate",
                GetAdditional(track, originalDateKey)
                    ?? ReadVorbisComment(tempPath, originalDateKey)
                    ?? FormatDate(track.OriginalReleaseDate));
        }
        else if (keys.OriginalDateViaProperty)
        {
            AddString(values, "OriginalDate", FormatDate(track.OriginalReleaseDate));
        }
        AddNumber(values, "TrackNumber", track.TrackNumber > 0 ? track.TrackNumber : null);
        AddNumber(values, "TrackTotal", track.TrackTotal > 0 ? track.TrackTotal : null);
        AddNumber(values, "DiscNumber", track.DiscNumber > 0 ? track.DiscNumber : null);
        AddNumber(values, "DiscTotal", track.DiscTotal > 0 ? track.DiscTotal : null);

        AddString(values, "Artists", GetAdditional(track, keys.Artists));
        AddString(values, "MbRecordingId", GetAdditional(track, keys.RecordingId));
        AddString(values, "MbReleaseTrackId", GetAdditional(track, keys.ReleaseTrackId));
        AddString(values, "MbReleaseId", GetAdditional(track, keys.ReleaseId));
        AddString(values, "MbReleaseGroupId", GetAdditional(track, keys.ReleaseGroupId));
        AddString(values, "MbArtistId", GetAdditional(track, keys.ArtistId));
        AddString(values, "MbAlbumArtistId", GetAdditional(track, keys.AlbumArtistId));
        AddString(values, "ReleaseType", GetAdditional(track, keys.ReleaseType));
        AddString(values, "ReleaseStatus", GetAdditional(track, keys.ReleaseStatus));
        AddString(values, "AcoustId", GetAdditional(track, keys.AcoustId));
        AddString(values, "Compilation", GetAdditional(track, keys.Compilation));
        AddString(values, "ReplayGainTrackGain", GetAdditional(track, keys.ReplayGainTrackGain));
        AddString(values, "ReplayGainTrackPeak", GetAdditional(track, keys.ReplayGainTrackPeak));

        // MP4's covr atom carries no picture type, so ATL reports it as Generic; the embed is verified by
        // the byte count of the single picture the writer left behind rather than by its type.
        if (track.EmbeddedPictures.Count > 0)
        {
            AddNumber(values, "FrontCover", track.EmbeddedPictures[0].PictureData?.Length);
        }

        return values;
    }

    /// <summary>The logical field names mapped to the values they must end up holding.</summary>
    private static Dictionary<string, string> ExpectedFields(TagSet tags, FormatKeys keys)
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);

        AddString(expected, "Title", tags.Title);
        AddString(expected, "Artist", tags.Artist);
        AddString(expected, "AlbumArtist", tags.AlbumArtist);
        AddString(expected, "Album", tags.Album);
        AddString(expected, "Date", tags.Date);
        AddString(expected, "Isrc", tags.Isrc);
        AddString(expected, "Genre", tags.Genre);
        AddString(expected, "Comment", tags.Comment);
        AddString(expected, "Lyrics", NormalizedLyrics(tags.Lyrics));
        if (keys.OriginalDate is not null || keys.OriginalDateViaProperty)
        {
            AddString(expected, "OriginalDate", tags.OriginalDate);
        }
        AddNumber(expected, "TrackNumber", tags.TrackNumber);
        AddNumber(expected, "TrackTotal", tags.TrackTotal);
        AddNumber(expected, "DiscNumber", tags.DiscNumber);
        AddNumber(expected, "DiscTotal", tags.DiscTotal);

        if (tags.Artists.Count > 0)
        {
            expected["Artists"] = JoinArtists(tags.Artists);
        }

        AddString(expected, "MbRecordingId", tags.MbRecordingId);
        AddString(expected, "MbReleaseTrackId", tags.MbReleaseTrackId);
        AddString(expected, "MbReleaseId", tags.MbReleaseId);
        AddString(expected, "MbReleaseGroupId", tags.MbReleaseGroupId);
        AddString(expected, "MbArtistId", tags.MbArtistId);
        AddString(expected, "MbAlbumArtistId", tags.MbAlbumArtistId);
        AddString(expected, "ReleaseType", tags.ReleaseType);
        AddString(expected, "ReleaseStatus", tags.ReleaseStatus);
        AddString(expected, "AcoustId", tags.AcoustId);

        if (tags.Compilation)
        {
            expected["Compilation"] = "1";
        }

        AddString(expected, "ReplayGainTrackGain", FormatGain(tags.ReplayGainTrackGainDb));
        AddString(expected, "ReplayGainTrackPeak", FormatPeak(tags.ReplayGainTrackPeak));

        if (tags.FrontCover is { Length: > 0 } cover)
        {
            AddNumber(expected, "FrontCover", cover.Length);
        }

        return expected;
    }

    /// <summary>The ATL <c>AdditionalFields</c> key of every logical field, per format.</summary>
    /// <remarks>
    /// ATL's convention for <c>AdditionalFields</c> is the <em>bare</em> field name in every format: an
    /// ID3v2 free-text field becomes a <c>TXXX</c> frame whose description is the key (writing
    /// <c>TXXX:…</c> would put that literal prefix into the description), and an MP4 free-text field
    /// becomes the <c>----:com.apple.iTunes:…</c> freeform atom whose name is the key. The on-disk
    /// frame/atom each value must end up in is the one in LIBRARY_OUTPUT §7.5, and the read-back tests
    /// assert it frame by frame.
    /// </remarks>
    internal sealed record FormatKeys(
        string Artists,
        string RecordingId,
        string ReleaseTrackId,
        string ReleaseId,
        string ReleaseGroupId,
        string ArtistId,
        string AlbumArtistId,
        string ReleaseType,
        string ReleaseStatus,
        string AcoustId,
        string Compilation,
        string CompilationValue,
        string? OriginalDate,
        bool OriginalDateViaProperty,
        MetaDataIOFactory.TagType CoverTagType,
        string? CoverNativeCode,
        string ReplayGainTrackGain = "REPLAYGAIN_TRACK_GAIN",
        string ReplayGainTrackPeak = "REPLAYGAIN_TRACK_PEAK");

    internal static readonly FormatKeys Id3Keys = new(
        Artists: "ARTISTS",
        RecordingId: "MusicBrainz Track Id",
        ReleaseTrackId: "MusicBrainz Release Track Id",
        ReleaseId: "MusicBrainz Album Id",
        ReleaseGroupId: "MusicBrainz Release Group Id",
        ArtistId: "MusicBrainz Artist Id",
        AlbumArtistId: "MusicBrainz Album Artist Id",
        ReleaseType: "MusicBrainz Album Type",
        ReleaseStatus: "MusicBrainz Album Status",
        AcoustId: "Acoustid Id",
        Compilation: "TCMP",
        CompilationValue: "1",
        OriginalDate: null,
        OriginalDateViaProperty: true,
        CoverTagType: MetaDataIOFactory.TagType.ANY,
        CoverNativeCode: null);

    internal static readonly FormatKeys Mp4Keys = Id3Keys with
    {
        Compilation = "cpil",
        OriginalDateViaProperty = false,
        CoverTagType = MetaDataIOFactory.TagType.ANY,
        CoverNativeCode = "covr",
    };

    internal static readonly FormatKeys VorbisKeys = new(
        Artists: "ARTISTS",
        RecordingId: "MUSICBRAINZ_TRACKID",
        ReleaseTrackId: "MUSICBRAINZ_RELEASETRACKID",
        ReleaseId: "MUSICBRAINZ_ALBUMID",
        ReleaseGroupId: "MUSICBRAINZ_RELEASEGROUPID",
        ArtistId: "MUSICBRAINZ_ARTISTID",
        AlbumArtistId: "MUSICBRAINZ_ALBUMARTISTID",
        ReleaseType: "RELEASETYPE",
        ReleaseStatus: "RELEASESTATUS",
        AcoustId: "ACOUSTID_ID",
        Compilation: "COMPILATION",
        CompilationValue: "1",

        // A literal field: ATL's OriginalReleaseDate property writes the Vorbis field as
        // "ORIGINALDATE " (trailing space), which Plex and Picard do not read — seen on the first
        // live import (2026-09-29).
        OriginalDate: "ORIGINALDATE",
        OriginalDateViaProperty: false,
        CoverTagType: MetaDataIOFactory.TagType.ANY,
        CoverNativeCode: null);

    internal static FormatKeys KeysFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp3" or ".aiff" or ".aif" or ".wav" or ".aac" => Id3Keys,
            ".m4a" or ".m4b" or ".mp4" => Mp4Keys,
            ".flac" or ".opus" or ".ogg" or ".oga" => VorbisKeys,
            _ => throw new NotSupportedException("The file's format is not one of MP3, FLAC, Opus or M4A."),
        };

    /// <summary>A track gain as ReplayGain readers expect it: invariant culture, signed, two decimals, <c>-8.52 dB</c>.</summary>
    internal static string? FormatGain(double? gainDb) =>
        gainDb is { } gain && double.IsFinite(gain)
            ? gain.ToString("+0.00;-0.00;+0.00", CultureInfo.InvariantCulture) + " dB"
            : null;

    /// <summary>A true peak as a linear value with six decimals, <c>1.047129</c>.</summary>
    internal static string? FormatPeak(double? peak) =>
        peak is { } value && double.IsFinite(value) && value >= 0
            ? value.ToString("0.000000", CultureInfo.InvariantCulture)
            : null;

    private static void WriteAdditional(Track track, string? key, string? value)
    {
        if (key is not null && Has(value))
        {
            track.AdditionalFields[key] = value;
        }
    }

    internal static string? GetAdditional(Track track, string key) =>
        track.AdditionalFields.TryGetValue(key, out var value) ? value : null;

    private static string? UnsynchronizedLyrics(Track track) =>
        track.Lyrics.FirstOrDefault(lyrics => Has(lyrics.UnsynchronizedLyrics))?.UnsynchronizedLyrics;

    /// <summary>
    /// ATL stores and splits multi-valued fields with its display separator, so a literal semicolon in
    /// an artist name would read back as two artists; MusicBrainz credit names never contain one.
    /// </summary>
    private static string JoinArtists(IReadOnlyList<string> artists) =>
        string.Join(Settings.DisplayValueSeparator, artists);

    /// <summary>
    /// Parses a <c>YYYY</c>, <c>YYYY-MM</c> or <c>YYYY-MM-DD</c> date. ATL exposes a date only as a
    /// <see cref="DateTime"/> and carries the written precision itself, so a partial date (year, or
    /// year and month) is padded to its first day here; the read-back comparison is prefix-based to
    /// match. §7.3/§7.5 care that every file of a folder holds the *same* date.
    /// </summary>
    private static bool TryParseDate(string? value, out DateTime date)
    {
        date = default;

        // Exactly the three shapes MusicBrainz dates come in: DateTime.TryParse does not accept a
        // bare year, which is what every pseudo-album carries.
        return Has(value)
            && DateTime.TryParseExact(
                value!.Trim(),
                ["yyyy", "yyyy-MM", "yyyy-MM-dd"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date);
    }

    /// <summary>
    /// The value of a Vorbis comment <paramref name="field"/> (<c>FIELD=value</c>, length-prefixed),
    /// found in the first 16 MB of the file — FLAC and Ogg keep their comments at the start, before
    /// the audio; an embedded cover can push them back by a few MB.
    /// </summary>
    internal static string? ReadVorbisComment(string path, string field)
    {
        var buffer = new byte[16 * 1024 * 1024];
        int read;
        using (var stream = File.OpenRead(path))
        {
            read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }

        var needle = System.Text.Encoding.ASCII.GetBytes(field + "=");
        var index = buffer.AsSpan(0, read).IndexOf(needle);

        // A Vorbis comment is "<uint32 little-endian length><FIELD=value>".
        if (index < 4)
        {
            return null;
        }

        var length = BitConverter.ToInt32(buffer, index - 4);
        var valueLength = length - needle.Length;

        return valueLength is > 0 and < 256 && index + length <= read
            ? System.Text.Encoding.UTF8.GetString(buffer, index + needle.Length, valueLength)
            : null;
    }

    internal static string? FormatYear(int? year) =>
        year is > 0 ? year.Value.ToString("0000", CultureInfo.InvariantCulture) : null;

    internal static string? FormatDate(DateTime? date) =>
        date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool SameValue(string field, string expected, string actual)
    {
        if (field is "Date" or "OriginalDate")
        {
            // ATL carries a date's precision separately; compare on the given string's own precision.
            return actual.StartsWith(expected, StringComparison.Ordinal)
                || expected.StartsWith(actual, StringComparison.Ordinal);
        }

        return string.Equals(expected, actual, StringComparison.Ordinal);
    }

    private static void AddString(Dictionary<string, string> values, string field, string? value)
    {
        if (Has(value))
        {
            values[field] = value;
        }
    }

    private static void AddNumber(Dictionary<string, string> values, string field, int? value)
    {
        if (value is { } number)
        {
            values[field] = number.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static bool Has([NotNullWhen(true)] string? value) => !string.IsNullOrWhiteSpace(value);

    /// <summary>
    /// Lyrics as they are written and expected back: <c>\n</c> line endings and no blank lines around
    /// the text. ATL trims a Vorbis comment when it reads one back, so text that starts or ends with a
    /// newline (LRCLIB serves such records) never read back equal, and the import failed.
    /// </summary>
    private static string? NormalizedLyrics(string? lyrics) =>
        Has(lyrics) ? lyrics.ReplaceLineEndings("\n").Trim() : null;

    private static TagWriteResult Failure(string error) => new(false, error, EmptyWritten);

    private static void DeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                // A copy of a read-only source keeps the attribute; deletion would fail without this.
                File.SetAttributes(tempPath, FileAttributes.Normal);
                File.Delete(tempPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leaving a stray temp file behind is preferable to failing a write that already succeeded.
        }
    }

    [LoggerMessage(EventId = 2101, Level = LogLevel.Debug, Message = "Tagged an audio file with {FieldCount} verified fields.")]
    private partial void LogTagged(int fieldCount);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Debug, Message = "Tagging failed; the original file was left untouched.")]
    private partial void LogFailed(Exception exception);
}
