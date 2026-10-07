using System.Text.Json;
using Wondarr.Core.Jobs;
using Microsoft.Extensions.Logging;

namespace Wondarr.Core.Albums;

/// <summary>
/// The background half of an album add: <c>AddAlbum</c> resolves the album's tracks and adds them as
/// songs. It runs as a command so the user can watch progress and the MusicBrainz rate limit is
/// respected by the shared resolvers.
/// </summary>
public sealed partial class AddAlbumCommandHandler : ICommandHandler
{
    /// <summary>The command name, as <c>POST /api/v1/command</c> names it.</summary>
    public const string CommandName = "AddAlbum";

    /// <summary>Command bodies are camelCase, like the API's own resources.</summary>
    private static readonly JsonSerializerOptions BodyJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IAlbumService _albums;
    private readonly ILogger<AddAlbumCommandHandler> _logger;

    /// <summary>Initialises a new instance of the <see cref="AddAlbumCommandHandler"/> class.</summary>
    /// <param name="albums">The album add.</param>
    /// <param name="logger">The logger.</param>
    public AddAlbumCommandHandler(IAlbumService albums, ILogger<AddAlbumCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(albums);
        ArgumentNullException.ThrowIfNull(logger);

        _albums = albums;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => CommandName;

    /// <inheritdoc />
    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var body = ReadBody(context.Body);

        LogStarted(_logger, body.Source, body.Id);

        var result = await _albums
            .AddAsync(
                new AlbumAddRequest
                {
                    Album = new AlbumRef(body.Source, body.Id),
                    TrackKeys = body.TrackKeys,
                    LibraryId = body.LibraryId,
                    QualityProfileId = body.QualityProfileId,
                    Monitored = body.Monitored ?? true,
                },
                context.ReportProgressAsync,
                cancellationToken)
            .ConfigureAwait(false);

        return result.Message;
    }

    /// <summary>Reads the command body, which the API wrote.</summary>
    private static AddAlbumBody ReadBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("An album add needs a body with the album's source and id.");
        }

        var parsed = JsonSerializer.Deserialize<AddAlbumBody>(body, BodyJson)
            ?? throw new ArgumentException("An album add needs a body with the album's source and id.");

        if (string.IsNullOrWhiteSpace(parsed.Source) || string.IsNullOrWhiteSpace(parsed.Id))
        {
            throw new ArgumentException("An album add needs the album's source and id.");
        }

        return parsed;
    }

    /// <summary>The body of an <c>AddAlbum</c> command, exactly as the API writes it.</summary>
    /// <param name="Source">The provider the album lives on.</param>
    /// <param name="Id">The album's id on that provider.</param>
    /// <param name="TrackKeys">The tracks to add, or <see langword="null"/> for all of them.</param>
    /// <param name="LibraryId">The library the songs land in.</param>
    /// <param name="QualityProfileId">The profile the songs are monitored against.</param>
    /// <param name="Monitored">Whether the songs are wanted.</param>
    private sealed record AddAlbumBody(
        string Source,
        string Id,
        IReadOnlyList<string>? TrackKeys,
        long? LibraryId,
        long? QualityProfileId,
        bool? Monitored);

    [LoggerMessage(Level = LogLevel.Information, Message = "Adding album {Source}/{Id} as songs")]
    private static partial void LogStarted(ILogger logger, string source, string id);
}
