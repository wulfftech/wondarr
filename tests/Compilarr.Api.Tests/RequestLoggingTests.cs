using System.Net;
using Compilarr.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Compilarr.Api.Tests;

public class RequestLoggingTests
{
    [Fact]
    public async Task A_request_is_logged_once_without_its_query_string()
    {
        using var factory = new CompilarrAppFactory();
        using var client = factory.CreateClient();
        var apiKey = factory.Services.GetRequiredService<IOptions<ServerOptions>>().Value.ApiKey;
        apiKey.Should().NotBeNullOrWhiteSpace();

        using var response = await client.GetAsync(new Uri($"/ping?apikey={apiKey}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var lines = await RequestLines(factory.ConfigDir);
        lines.Should().HaveCount(1, $"diagnostic: {Diagnostic(factory.ConfigDir)}");

        var line = lines[0];
        line.Should().Contain("/ping");
        line.Should().NotContain(apiKey);
        line.Should().NotContain("apikey=");
    }

    private static string Diagnostic(string configDir)
    {
        var logsDir = Path.Combine(configDir, "logs");

        if (!Directory.Exists(logsDir))
        {
            return $"no logs dir; config dir has: {string.Join(", ", Directory.Exists(configDir) ? Directory.GetFileSystemEntries(configDir) : ["<missing>"])}";
        }

        var files = Directory.GetFiles(logsDir, "*");
        return string.Join(" || ", files.Select(file => $"{Path.GetFileName(file)}: {string.Join('\n', ReadShared(file))}"));
    }

    /// <summary>
    /// Reads the request-log lines, waiting briefly for the file sink: the request logger writes
    /// when the response completes, which can be a moment after the client sees it.
    /// </summary>
    private static async Task<IReadOnlyList<string>> RequestLines(string configDir)
    {
        var logsDir = Path.Combine(configDir, "logs");

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var lines = Directory.Exists(logsDir)
                ? Directory.GetFiles(logsDir, "compilarr-*.json")
                    .SelectMany(ReadShared)
                    .Where(line => line.Contains("HTTP GET", StringComparison.Ordinal))
                    .ToArray()
                : [];

            if (lines.Length > 0)
            {
                return lines;
            }

            await Task.Delay(100);
        }

        return [];
    }

    // The file sink keeps the log open for writing; share it instead of failing on Windows.
    private static string[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}
