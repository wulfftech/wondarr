using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Jobs;

namespace Wondarr.Core.Media;

/// <summary>
/// The <c>ConvertFiles</c> command: converts the songs a request names, or a library's songs, one at a
/// time (ffmpeg is the bottleneck, and one song's conversion holds that song's lock), and reports the
/// counts. One song's failure never stops the others.
/// </summary>
public sealed partial class ConvertFilesCommandHandler : ICommandHandler
{
    /// <summary>The command name the queue and the API use.</summary>
    public const string CommandName = "ConvertFiles";

    private static readonly JsonSerializerOptions BodyJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IFileConverter _converter;
    private readonly ILogger<ConvertFilesCommandHandler> _logger;

    /// <summary>Initialises a new instance of the <see cref="ConvertFilesCommandHandler"/> class.</summary>
    /// <param name="converter">The converter.</param>
    /// <param name="logger">The logger.</param>
    public ConvertFilesCommandHandler(IFileConverter converter, ILogger<ConvertFilesCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(converter);
        ArgumentNullException.ThrowIfNull(logger);

        _converter = converter;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => CommandName;

    /// <inheritdoc />
    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = ReadRequest(context.Body);
        var songIds = await _converter.ResolveSongsAsync(request, cancellationToken).ConfigureAwait(false);
        var counts = new Dictionary<ConvertOutcome, int>();
        string? firstProblem = null;
        var done = 0;

        foreach (var songId in songIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ConvertSongResult result;

            try
            {
                result = await _converter.ConvertAsync(songId, request.RuleJson, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogSongFailed(_logger, songId, exception);
                result = new ConvertSongResult(songId, ConvertOutcome.Failed, exception.Message);
            }

            counts[result.Outcome] = counts.GetValueOrDefault(result.Outcome) + 1;

            if (firstProblem is null && result.Outcome is ConvertOutcome.Failed or ConvertOutcome.Refused)
            {
                firstProblem = string.Create(CultureInfo.InvariantCulture, $"song {songId}: {result.Reason}");
            }

            done++;
            await context
                .ReportProgressAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Converted {counts.GetValueOrDefault(ConvertOutcome.Converted)} of {songIds.Count} ({done} done)"))
                .ConfigureAwait(false);
        }

        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"{counts.GetValueOrDefault(ConvertOutcome.Converted)} converted, {counts.GetValueOrDefault(ConvertOutcome.Skipped)} skipped, {counts.GetValueOrDefault(ConvertOutcome.Refused)} refused, {counts.GetValueOrDefault(ConvertOutcome.Failed)} failed");

        return firstProblem is null ? summary : string.Concat(summary, " (first problem: ", firstProblem, ")");
    }

    private static ConvertRequest ReadRequest(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("The ConvertFiles command needs a body naming songIds or a libraryId.");
        }

        ConvertFilesBody? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<ConvertFilesBody>(body, BodyJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The ConvertFiles command body is not valid JSON.", exception);
        }

        return parsed is null
            ? throw new ArgumentException("The ConvertFiles command body is empty.")
            : new ConvertRequest(
                parsed.SongIds,
                parsed.LibraryId,
                parsed.Rule is { ValueKind: JsonValueKind.Object } rule ? rule.GetRawText() : null);
    }

    private sealed record ConvertFilesBody(string? Name, List<long>? SongIds, long? LibraryId, JsonElement? Rule);

    [LoggerMessage(Level = LogLevel.Error, Message = "Converting song {SongId} failed unexpectedly")]
    private static partial void LogSongFailed(ILogger logger, long songId, Exception exception);
}
