using System.Text.Json;
using FluentAssertions;
using Wondarr.Core.Organizer;
using Xunit;

namespace Wondarr.Core.Tests.Organizer;

/// <summary>
/// The golden cases of <c>tests/fixtures/naming.json</c> (LIBRARY_OUTPUT.md §7.1). The fixture is the
/// authority: a disagreement here is a bug in the engine, not in the fixture.
/// </summary>
public class NamingTemplateGoldenTests
{
    private static readonly JsonSerializerOptions FixtureOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly Fixture Golden = LoadFixture();

    /// <summary>The fixture's case names, so the theory data stays serializable and the runs stay readable.</summary>
    public static TheoryData<string> RenderCaseNames()
    {
        var data = new TheoryData<string>();

        foreach (var testCase in Golden.Cases)
        {
            data.Add(testCase.Name);
        }

        return data;
    }

    /// <summary>The fixture's error-case names.</summary>
    public static TheoryData<string> ErrorCaseNames()
    {
        var data = new TheoryData<string>();

        foreach (var testCase in Golden.Errors)
        {
            data.Add(testCase.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RenderCaseNames))]
    public void Renders_the_golden_path_of_a_fixture_case(string name)
    {
        var testCase = Golden.Cases.Single(candidate => candidate.Name == name);

        var actual = NamingTemplate.Render(testCase.Template, testCase.Values, testCase.Options);

        actual.Should().Be(testCase.Expected, $"case '{name}'");
    }

    [Theory]
    [MemberData(nameof(ErrorCaseNames))]
    public void Rejects_a_template_the_fixture_rejects(string name)
    {
        var testCase = Golden.Errors.Single(candidate => candidate.Name == name);

        var result = NamingTemplate.Validate(testCase.Template);

        result.IsValid.Should().BeFalse($"case '{name}'");
        result.Errors.Should().Contain(
            error => error.Contains(testCase.MessageContains, StringComparison.Ordinal),
            $"case '{name}'");

        var render = () => NamingTemplate.Render(testCase.Template, NamingTemplateTests.SampleValues());

        render.Should().Throw<ArgumentException>($"case '{name}'")
            .Which.Message.Should().Contain(testCase.MessageContains);
    }

    private static Fixture LoadFixture() =>
        JsonSerializer.Deserialize<Fixture>(File.ReadAllText(FindFixture("naming.json")), FixtureOptions)
        ?? throw new InvalidOperationException("tests/fixtures/naming.json is empty.");

    /// <summary>
    /// Walks up from the test output directory to <c>tests/fixtures</c>: the fixture is shared with the
    /// repository's other tests and is not copied into the build output.
    /// </summary>
    private static string FindFixture(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "fixtures", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not find tests/fixtures/{fileName} above {AppContext.BaseDirectory}.");
    }

    private sealed record Fixture(IReadOnlyList<GoldenCase> Cases, IReadOnlyList<ErrorCase> Errors);

    private sealed record GoldenCase(string Name, string Template, NamingValues Values, NamingOptions? Options, string Expected);

    private sealed record ErrorCase(string Name, string Template, string MessageContains);
}