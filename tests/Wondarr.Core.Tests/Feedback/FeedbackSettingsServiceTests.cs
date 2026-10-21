using System.Collections;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Wondarr.Core.Configuration;
using Wondarr.Core.Feedback;
using Wondarr.Core.Logging;
using Xunit;

namespace Wondarr.Core.Tests.Feedback;

/// <summary>The Contribute with feedback card's service, with the config writer and GitHub check faked.</summary>
public sealed class FeedbackSettingsServiceTests
{
    private const string Token = "ghp_settingstoken0123456789abcdefghij";

    private static readonly CancellationToken None = CancellationToken.None;

    private readonly FeedbackOptions _options = new();
    private readonly IConfigFileWriter _writer = Substitute.For<IConfigFileWriter>();
    private readonly IFeedbackService _feedback = Substitute.For<IFeedbackService>();
    private readonly SecretRegistry _secrets = new();
    private readonly List<IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>> _writes = [];

    /// <summary>What the registry made of the token at the moment of each write.</summary>
    private readonly List<string> _redactedAtWrite = [];

    public FeedbackSettingsServiceTests()
    {
        _writer
            .UpdateSectionsAsync(
                Arg.Do<IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>>(sections =>
                {
                    _writes.Add(sections);
                    _redactedAtWrite.Add(_secrets.Redact(Token));
                }),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public void It_reports_whether_a_token_is_set_and_never_the_token()
    {
        Service().Current().Should().Be(new FeedbackSettings(false, false));

        _options.GitHubToken = Token;

        Service().Current().Should().Be(new FeedbackSettings(true, false));
    }

    [Fact]
    public async Task A_token_is_trimmed_registered_as_a_secret_and_written_to_the_feedback_section()
    {
        var result = await Service().UpdateAsync($"  {Token}\n", None);

        result.Success.Should().BeTrue();

        var write = _writes.Should().ContainSingle().Subject;

        write.Should().ContainKey("feedback");
        write["feedback"].Should().Equal(new Dictionary<string, object?> { ["github_token"] = Token });

        // Known before the write: the reload that follows it may log, and must not print the token.
        _redactedAtWrite.Should().Equal("(removed)");
    }

    [Fact]
    public async Task An_empty_token_removes_it_and_an_absent_one_changes_nothing()
    {
        (await Service().UpdateAsync(null, None)).Success.Should().BeTrue();
        _writes.Should().BeEmpty();

        (await Service().UpdateAsync("   ", None)).Success.Should().BeTrue();

        _writes.Should().ContainSingle().Which["feedback"]["github_token"].Should().BeNull();
    }

    [Theory]
    [InlineData("ghp_abc def")]
    [InlineData("Authorization: Bearer ghp_abc")]
    [InlineData("ghp_abc\nghp_def")]
    public async Task Something_with_spaces_or_line_breaks_in_it_is_refused(string pasted)
    {
        var result = await Service().UpdateAsync(pasted, None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("spaces or line breaks");
        _writes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ghp_abc\u2019")] // a smart quote from a chat app
    [InlineData("ghp_\u200Babc")] // a zero-width space
    [InlineData("ghp-abc:def")]
    public async Task Characters_a_github_token_never_has_are_refused(string pasted)
    {
        var result = await Service().UpdateAsync(pasted, None);

        result.Success.Should().BeFalse();
        _writes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_token_too_long_to_be_one_is_refused()
    {
        var result = await Service().UpdateAsync(new string('a', FeedbackSettingsService.MaxTokenLength + 1), None);

        result.Success.Should().BeFalse();
        _writes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_token_the_environment_owns_is_locked_and_a_change_names_the_variable()
    {
        _options.GitHubToken = Token;
        var service = Service(new Hashtable { ["APP__FEEDBACK__GITHUB_TOKEN"] = Token });

        service.Current().Should().Be(new FeedbackSettings(true, true));

        var result = await service.UpdateAsync("ghp_other", None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("APP__FEEDBACK__GITHUB_TOKEN");
        _writes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_test_checks_the_typed_token_else_the_stored_one()
    {
        _feedback.CheckTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new FeedbackTokenCheck(true, "fine"));

        (await Service().TestAsync(null, None)).Should().Be(new FeedbackTokenCheck(false, "There is no token to test; paste one first."));
        await _feedback.DidNotReceiveWithAnyArgs().CheckTokenAsync(default!, default);

        (await Service().TestAsync($" {Token} ", None)).Ok.Should().BeTrue();
        await _feedback.Received(1).CheckTokenAsync(Token, Arg.Any<CancellationToken>());

        _options.GitHubToken = "ghp_storedtoken0123456789";
        await Service().TestAsync("", None);
        await _feedback.Received(1).CheckTokenAsync("ghp_storedtoken0123456789", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_test_of_something_that_cannot_be_a_token_asks_github_nothing()
    {
        var result = await Service().TestAsync("two words", None);

        result.Ok.Should().BeFalse();
        await _feedback.DidNotReceiveWithAnyArgs().CheckTokenAsync(default!, default);
    }

    private FeedbackSettingsService Service(IDictionary? environment = null)
    {
        var options = Substitute.For<IOptionsMonitor<FeedbackOptions>>();
        options.CurrentValue.Returns(_ => _options);

        return new FeedbackSettingsService(
            options,
            _writer,
            _feedback,
            _secrets,
            environment ?? new Hashtable(),
            NullLogger<FeedbackSettingsService>.Instance);
    }
}
