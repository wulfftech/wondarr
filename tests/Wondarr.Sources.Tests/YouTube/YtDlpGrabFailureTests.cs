using Wondarr.Core.Searching;
using Wondarr.Sources.YouTube;
using FluentAssertions;
using Xunit;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>
/// The classification the search service's backoff reads: a <see cref="YtDlpException"/>'s kind maps
/// to the action the queue takes — retry-later kinds never blocklist the video, blocklist kinds
/// always do. These tests pin the mapping through the real exception, not a stub of it.
/// </summary>
public class YtDlpGrabFailureTests
{
    [Theory]
    [InlineData(YtDlpErrorKind.BotCheck)]
    [InlineData(YtDlpErrorKind.RateLimited)]
    [InlineData(YtDlpErrorKind.TimedOut)]
    [InlineData(YtDlpErrorKind.ToolMissing)]
    [InlineData(YtDlpErrorKind.Unknown)]
    public void Retry_later_kinds_never_blocklist_the_candidate(YtDlpErrorKind kind)
    {
        var failure = Failure(kind);

        failure.Should().BeAssignableTo<ISourceGrabFailure>()
            .Which.BlocklistCandidate.Should().BeFalse(
                "the source had a bad moment; the video itself is not dead");
    }

    [Theory]
    [InlineData(YtDlpErrorKind.GeoRestricted)]
    [InlineData(YtDlpErrorKind.AgeGated)]
    [InlineData(YtDlpErrorKind.PrivateOrUnavailable)]
    public void Blocklist_kinds_blocklist_the_candidate(YtDlpErrorKind kind)
    {
        var failure = Failure(kind);

        failure.Should().BeAssignableTo<ISourceGrabFailure>()
            .Which.BlocklistCandidate.Should().BeTrue("the video is dead for this song");
    }

    [Fact]
    public void The_message_is_carried_for_the_blocklist_row()
    {
        var failure = Failure(YtDlpErrorKind.GeoRestricted, "the uploader has not made this video available");

        failure.Should().BeAssignableTo<ISourceGrabFailure>()
            .Which.Message.Should().Be("the uploader has not made this video available",
                "the blocklist row records the classified detail, verbatim");
    }

    private static YtDlpException Failure(YtDlpErrorKind kind, string? detail = null) =>
        new("abc123", kind, detail ?? $"the classified detail of {kind}");
}
