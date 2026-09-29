using Wondarr.Sources.Slskd;
using FluentAssertions;
using Xunit;

namespace Wondarr.Sources.Tests.Slskd;

public class SlskdLogWatcherTests
{
    [Theory]
    // The lines recorded live from the bundled slskd on ch01 on 2026-09-29.
    [InlineData("[23:05:36 INF] Logged in to the Soulseek server as wondarr-soulseek", SlskdLogSignal.LoggedIn)]
    [InlineData("[23:00:14 ERR] Disconnected from the Soulseek server: invalid username or password", SlskdLogSignal.InvalidCredentials)]
    [InlineData("[23:00:14 ERR] Failed to reconnect: The server rejected login attempt: INVALIDUSERNAME", SlskdLogSignal.InvalidCredentials)]
    [InlineData("Disconnected from the Soulseek server: another client logged in using the same username", SlskdLogSignal.DuplicateLogin)]
    [InlineData("[23:41:02 ERR] Failed to reconnect: The server rejected login attempt: INVALIDPASS", SlskdLogSignal.InvalidCredentials)]
    public void Classifies_the_login_lines_slskd_writes(string line, SlskdLogSignal expected)
    {
        SlskdLogWatcher.Classify(line).Should().Be(expected);
    }

    [Theory]
    [InlineData("LOGGED IN TO THE SOULSEEK SERVER AS wondarr-soulseek", SlskdLogSignal.LoggedIn)]
    [InlineData("logged in to the soulseek server as wondarr-soulseek", SlskdLogSignal.LoggedIn)]
    [InlineData("[23:00:14 ERR] failed to reconnect: the server rejected login attempt: invalidusername", SlskdLogSignal.InvalidCredentials)]
    [InlineData(
        "[23:00:14 ERR] DISCONNECTED FROM THE SOULSEEK SERVER: ANOTHER CLIENT LOGGED IN USING THE SAME USERNAME",
        SlskdLogSignal.DuplicateLogin)]
    public void Classifies_without_caring_about_case(string line, SlskdLogSignal expected)
    {
        SlskdLogWatcher.Classify(line).Should().Be(expected);
    }

    [Theory]
    // A peer's search text and a filename both reach this method through slskd's log. Only slskd's
    // own message may decide the login outcome, and only from the start of the line.
    [InlineData("[23:00:14 INF] Search request for 'another client logged in using the same username' from peer")]
    [InlineData("[23:00:14 INF] Search request for 'Logged in to the Soulseek server as foo' from peer")]
    [InlineData("[23:00:14 INF] Download of 'invalid username or password.mp3' started")]
    [InlineData("[23:00:14 INF] Rejected: Failed to reconnect: The server rejected login attempt: INVALIDUSERNAME")]
    [InlineData("peer asked: Disconnected from the Soulseek server: another client logged in using the same username")]
    public void A_peer_cannot_fake_a_login_outcome_by_writing_its_own_text(string line)
    {
        SlskdLogWatcher.Classify(line).Should().BeNull();
    }

    [Theory]
    // The prefix is optional, but only slskd's own "[HH:mm:ss LVL] " shape is removed; a bracketed
    // file name is left where it is.
    [InlineData("[23:00:14 WRN] Logged in to the Soulseek server as wondarr-soulseek", SlskdLogSignal.LoggedIn)]
    [InlineData("[not-a-level] Logged in to the Soulseek server as wondarr-soulseek", null)]
    public void Only_slskds_own_level_prefix_is_stripped(string line, SlskdLogSignal? expected)
    {
        SlskdLogWatcher.Classify(line).Should().Be(expected);
    }

    [Fact]
    public void A_duplicate_login_kick_is_not_read_as_a_login()
    {
        // The kick line says "logged in" as well; the kick is the outcome that matters.
        SlskdLogWatcher
            .Classify("[23:00:14 ERR] Disconnected from the Soulseek server: another client logged in using the same username")
            .Should().Be(SlskdLogSignal.DuplicateLogin);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[23:05:36 INF] Now listening on: http://127.0.0.1:5030")]
    [InlineData("[23:05:36 INF] Connected to the Soulseek server")]
    [InlineData("[23:05:36 INF] Sharing 42 directories")]
    [InlineData("[23:05:36 INF] Disconnected from the Soulseek server: timed out")]
    public void Says_nothing_about_lines_that_are_not_about_the_login(string line)
    {
        SlskdLogWatcher.Classify(line).Should().BeNull();
    }
}
