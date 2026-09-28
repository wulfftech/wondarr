using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Compilarr.Api.Tests;

/// <summary>
/// Keeps <c>docs/api/openapi.json</c> in step with the API. The frontend generates its API types
/// from that file (P0-08), so a change to a controller that is not committed here would silently
/// leave the frontend behind.
/// <para>
/// Run with <c>COMPILARR_UPDATE_OPENAPI=1</c> to rewrite the snapshot instead of asserting.
/// </para>
/// </summary>
public sealed class OpenApiSnapshotTests : IDisposable
{
    private const string UpdateEnvironmentVariable = "COMPILARR_UPDATE_OPENAPI";
    private const string SnapshotRelativePath = "docs/api/openapi.json";
    private const string SolutionFileName = "Compilarr.sln";

    private readonly CompilarrAppFactory _factory = new();

    [Fact]
    public async Task The_openapi_document_matches_the_committed_snapshot()
    {
        var document = await FetchDocumentAsync();

        // AddOpenApi("v1") names it "v1"; the transformer fixes the title so the file does not
        // depend on the host or the build.
        document.Should().Contain("\"title\": \"Compilarr\"");
        document.Should().Contain("\"/api/v1/auth/user\"");
        document.Should().NotContain("\"servers\"");

        // /api/v1/system/status is owned by P0-04b and is not on this branch yet. Add the assertion
        // back — and regenerate the snapshot — once P0-04b has merged.

        var snapshotPath = Path.Combine(FindRepositoryRoot(), SnapshotRelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (Environment.GetEnvironmentVariable(UpdateEnvironmentVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            await File.WriteAllTextAsync(snapshotPath, document, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return;
        }

        File.Exists(snapshotPath).Should().BeTrue(
            $"{SnapshotRelativePath} is committed; regenerate it with {UpdateEnvironmentVariable}=1");

        var committed = await File.ReadAllTextAsync(snapshotPath);

        document.Should().Be(
            committed,
            $"the API changed; regenerate {SnapshotRelativePath} with {UpdateEnvironmentVariable}=1");
    }

    public void Dispose() => _factory.Dispose();

    /// <summary>Fetches the document and pretty-prints it so the diff is readable.</summary>
    private async Task<string> FetchDocumentAsync()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", _factory.ApiKey);

        using var response = await client.GetAsync(new Uri("/docs/v1/openapi.json", UriKind.Relative));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var json = await response.Content.ReadAsStringAsync();

        return PrettyPrint(json);
    }

    /// <summary>
    /// Two-space indent, <c>\n</c> line endings and a trailing newline, so the snapshot is identical
    /// on every platform and ends like every other text file in the repository.
    /// </summary>
    private static string PrettyPrint(string json)
    {
        using var parsed = JsonDocument.Parse(json);

        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            parsed.WriteTo(writer);
        }

        var pretty = Encoding.UTF8.GetString(buffer.ToArray())
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\n');

        return pretty + "\n";
    }

    /// <summary>Walks up from the test binaries to the directory holding the solution file.</summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull($"the repository root is the directory holding {SolutionFileName}");

        return directory!.FullName;
    }
}
