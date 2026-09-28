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
    [InlineData("Another Client Logged In Using The Same Username", SlskdLogSignal.DuplicateLogin)]
    [InlineData("INVALIDUSERNAME", SlskdLogSignal.InvalidCredentials)]
    public void Classifies_without_caring_about_case(string line, SlskdLogSignal expected)
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
