using System.Text;
using ATL;
using Xunit;

namespace Wondarr.Core.Tests.Tagging;

/// <summary>Copies a recorded fixture into a per-test temporary directory and finds written files.</summary>
public sealed class TestMedia : IDisposable
{
    /// <summary>A minimal, structurally valid 1x1 JPEG (used as the front cover).</summary>
    public static readonly byte[] CoverJpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAx" +
        "NDQ0Hyc5PTgyPC4zNDL/wAALCAABAAEBAREA/8QAFAABAAAAAAAAAAAAAAAAAAAACf/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEA" +
        "AD8AKp//2Q==");

    /// <summary>The four formats the tag writer must handle.</summary>
    public static readonly TheoryData<string> Formats =
    [
        "tone-320.mp3",
        "tone.flac",
        "tone-256.m4a",
        "tone-160.opus",
    ];

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("wondarr-tag-");

    /// <summary>Gets the temporary directory this test owns.</summary>
    public string TempDirectory => _directory.FullName;

    /// <summary>Copies a fixture into the temporary directory and returns the copy's path.</summary>
    public string Copy(string fixtureName, string? targetName = null)
    {
        var target = Path.Combine(_directory.FullName, targetName ?? fixtureName);
        File.Copy(FixtureMedia.Path(fixtureName), target, overwrite: true);
        return target;
    }

    /// <summary>Lists the tag writer's temporary copies still present in the directory.</summary>
    public string[] TempFilesLeftBehind() =>
        System.IO.Directory.GetFiles(_directory.FullName, ".wondarr-tag-*");

    /// <summary>Reads the file's ID3v2 frame ids and the printable part of each frame body.</summary>
    public static List<(string Id, string Text)> Id3Frames(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var frames = new List<(string Id, string Text)>();
        var body = bytes;
        if (bytes.Length > 10 && bytes[0] == 'I' && bytes[1] == 'D' && bytes[2] == '3')
        {
            var size = ((bytes[6] & 0x7F) << 21) | ((bytes[7] & 0x7F) << 14) | ((bytes[8] & 0x7F) << 7) | (bytes[9] & 0x7F);
            body = bytes[10..(10 + size)];
        }

        var text = Encoding.Latin1.GetString(body);
        var index = 0;
        while (index + 10 <= body.Length && body[index] != 0)
        {
            var id = text.Substring(index, 4);
            var size = ((body[index + 4] & 0x7F) << 21) | ((body[index + 5] & 0x7F) << 14) | ((body[index + 6] & 0x7F) << 7) | (body[index + 7] & 0x7F);
            var start = index + 10;
            var payload = text.Substring(start, Math.Min(size, Math.Max(0, body.Length - start)));
            frames.Add((id, new string(payload.Where(c => c is ';' or ':' or '/' or '.' || (c >= ' ' && c < 0x7F)).ToArray())));
            index = start + size;
        }

        return frames;
    }

    /// <summary>Reads the raw bytes of a file, for byte-level assertions.</summary>
    public static byte[] Bytes(string path) => File.ReadAllBytes(path);

    /// <summary>Probes a file with ATL for its duration and bitrate.</summary>
    public static (double DurationMs, int Bitrate) Probe(string path)
    {
        var track = new Track(path);
        return (track.DurationMs, track.Bitrate);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            foreach (var file in System.IO.Directory.GetFiles(_directory.FullName, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            _directory.Delete(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory under the OS temp folder is harmless.
        }
    }
}
