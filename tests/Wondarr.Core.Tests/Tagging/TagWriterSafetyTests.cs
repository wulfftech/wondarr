using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Tagging;
using Xunit;

namespace Wondarr.Core.Tests.Tagging;

/// <summary>A failed write must leave the original file byte-identical and no temporary copy behind.</summary>
public sealed class TagWriterSafetyTests
{
    private static readonly TagSet Tags = new() { Title = "Get Lucky", Artist = "Daft Punk", Album = "Random Access Memories" };

    private static TagWriter Writer => new(NullLogger<TagWriter>.Instance);

    [Fact]
    public async Task Leaves_a_read_only_file_untouched_and_returns_failure()
    {
        using var media = new TestMedia();
        var path = media.Copy("tone-320.mp3");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var before = Hash(path);
        var result = await Writer.WriteAsync(path, Tags, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
        result.Written.Should().BeEmpty();
        Hash(path).Should().Be(before);
        media.TempFilesLeftBehind().Should().BeEmpty();
    }

    [Fact]
    public async Task Leaves_a_corrupt_file_untouched_and_returns_failure()
    {
        using var media = new TestMedia();
        var path = media.Copy("garbage.mp3");

        var before = Hash(path);
        var result = await Writer.WriteAsync(path, Tags, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
        Hash(path).Should().Be(before);
        media.TempFilesLeftBehind().Should().BeEmpty();
    }

    [Fact]
    public async Task Rejects_a_missing_file_without_writing_anything()
    {
        using var media = new TestMedia();
        var path = Path.Combine(media.TempDirectory, "does-not-exist.mp3");

        var result = await Writer.WriteAsync(path, Tags, CancellationToken.None);

        result.Success.Should().BeFalse();
        media.TempFilesLeftBehind().Should().BeEmpty();
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}