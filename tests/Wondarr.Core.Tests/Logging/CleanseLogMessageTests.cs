using System.Text.Json;
using Wondarr.Core.Logging;
using FluentAssertions;
using Xunit;

namespace Wondarr.Core.Tests.Logging;

public class CleanseLogMessageTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };
    [Theory]
    [MemberData(nameof(Cases))]
    public void Replaces_secrets_but_keeps_the_rest_of_the_message(string input, string expected)
    {
        CleanseLogMessage.Cleanse(input).Should().Be(expected);
    }

    [Fact]
    public void Leaves_null_and_blank_messages_alone()
    {
        CleanseLogMessage.Cleanse("   ").Should().Be("   ");
    }

    public static TheoryData<string, string> Cases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "logging", "cleanse.json");
        File.Exists(path).Should().BeTrue($"{path} is copied to the output directory");

        var fixture = JsonSerializer.Deserialize<CleanseFixture>(File.ReadAllText(path), SerializerOptions);

        fixture.Should().NotBeNull();

        var data = new TheoryData<string, string>();

        foreach (var @case in fixture!.Cases)
        {
            data.Add(@case.Input, @case.Expected);
        }

        return data;
    }

    private sealed record CleanseFixture(IReadOnlyList<CleanseCase> Cases);

    private sealed record CleanseCase(string Input, string Expected);
}
