using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Logging;
using Wondarr.Core.Notifications;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Wondarr.Core.Indexers;

/// <summary>The values a create or an update carries.</summary>
/// <param name="Name">The display name.</param>
/// <param name="Type">The indexer type's name.</param>
/// <param name="Protocol">The protocol the row chooses, when its type serves both; otherwise ignored.</param>
/// <param name="Enabled">Whether searches ask this indexer.</param>
/// <param name="Priority">The ask order; a lower number is asked first.</param>
/// <param name="DownloadClientId">The client this indexer's grabs go to, or <see langword="null"/>.</param>
/// <param name="Settings">The indexer type's settings.</param>
/// <param name="Id">The row a test is about, so a masked secret can be kept from it, or <see langword="null"/>.</param>
public sealed record IndexerDraft(
    string Name,
    string Type,
    DownloadProtocol? Protocol,
    bool Enabled,
    int Priority,
    long? DownloadClientId,
    JsonElement Settings,
    long? Id = null);

/// <summary>
/// An indexer could not be stored: the values the user sent break one of the rules the API reports
/// as a <c>400</c>.
/// </summary>
public sealed class IndexerValidationException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="IndexerValidationException"/> class.</summary>
    /// <param name="field">The resource field the message belongs to, for example <c>type</c>.</param>
    /// <param name="detail">The sentence to show the user.</param>
    public IndexerValidationException(string field, string detail)
        : base(detail) => Field = field;

    /// <summary>Gets the field the message belongs to.</summary>
    public string Field { get; }

    /// <summary>Gets the sentence to show the user.</summary>
    public string Detail => Message;
}

/// <summary>The stored indexers the API reads and writes.</summary>
public interface IIndexerService
{
    /// <summary>Lists every indexer, ordered by id.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlyList<Indexer>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Reads one indexer, or <see langword="null"/>.</summary>
    /// <param name="id">The indexer id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<Indexer?> GetAsync(long id, CancellationToken cancellationToken);

    /// <summary>Stores a new indexer.</summary>
    /// <param name="draft">The values to store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<Indexer> CreateAsync(IndexerDraft draft, CancellationToken cancellationToken);

    /// <summary>Replaces an indexer, or returns <see langword="null"/> when there is no such row.</summary>
    /// <param name="id">The indexer id.</param>
    /// <param name="draft">The values to store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<Indexer?> UpdateAsync(long id, IndexerDraft draft, CancellationToken cancellationToken);

    /// <summary>Deletes an indexer, returning whether there was one.</summary>
    /// <param name="id">The indexer id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken);

    /// <summary>Connects with the draft's settings and reports whether the far end answered.</summary>
    /// <param name="draft">The indexer to test.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    Task<ProviderTestResult> TestAsync(IndexerDraft draft, CancellationToken cancellationToken);
}

/// <summary>
/// The rules an indexer is stored under: the type exists, the name is not blank, the protocol is the
/// type's own (or the row's choice when the type serves both), the named download client exists and
/// serves the same protocol, and the type accepts the settings.
/// </summary>
public sealed partial class IndexerService : IIndexerService
{
    private readonly WondarrDbContext _database;
    private readonly IReadOnlyList<IIndexerType> _types;
    private readonly ISecretRegistry _secrets;
    private readonly ILogger<IndexerService> _logger;

    /// <summary>Initialises a new instance of the <see cref="IndexerService"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="types">Every registered indexer type.</param>
    /// <param name="secrets">Where secret settings are registered so the log pipeline can redact them.</param>
    /// <param name="logger">The log sink.</param>
    public IndexerService(
        WondarrDbContext database,
        IEnumerable<IIndexerType> types,
        ISecretRegistry secrets,
        ILogger<IndexerService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _types = [.. types];
        _secrets = secrets;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Indexer>> ListAsync(CancellationToken cancellationToken)
    {
        var indexers = await _database.Indexers
            .OrderBy(indexer => indexer.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var indexer in indexers)
        {
            RegisterSecrets(indexer);
        }

        return indexers;
    }

    /// <inheritdoc />
    public async Task<Indexer?> GetAsync(long id, CancellationToken cancellationToken)
    {
        var indexer = await _database.Indexers
            .FirstOrDefaultAsync(indexer => indexer.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (indexer is not null)
        {
            RegisterSecrets(indexer);
        }

        return indexer;
    }

    /// <inheritdoc />
    public async Task<Indexer> CreateAsync(IndexerDraft draft, CancellationToken cancellationToken)
    {
        var (type, protocol) = await ValidateAsync(draft, cancellationToken).ConfigureAwait(false);

        var indexer = new Indexer
        {
            Name = draft.Name.Trim(),
            Type = type.Type,
            Protocol = protocol,
            Enabled = draft.Enabled,
            Priority = draft.Priority,
            DownloadClientId = draft.DownloadClientId,
            Settings = NotificationSecrets.Merge(draft.Settings, null, type.Fields),
        };

        RegisterSecrets(indexer);

        _database.Indexers.Add(indexer);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogSaved(_logger, indexer.Id, indexer.Type);

        return indexer;
    }

    /// <inheritdoc />
    public async Task<Indexer?> UpdateAsync(long id, IndexerDraft draft, CancellationToken cancellationToken)
    {
        var indexer = await GetAsync(id, cancellationToken).ConfigureAwait(false);

        if (indexer is null)
        {
            return null;
        }

        var (type, protocol) = await ValidateAsync(draft, cancellationToken).ConfigureAwait(false);

        indexer.Name = draft.Name.Trim();
        indexer.Type = type.Type;
        indexer.Protocol = protocol;
        indexer.Enabled = draft.Enabled;
        indexer.Priority = draft.Priority;
        indexer.DownloadClientId = draft.DownloadClientId;
        indexer.Settings = NotificationSecrets.Merge(draft.Settings, indexer.Settings, type.Fields);

        RegisterSecrets(indexer);

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogSaved(_logger, indexer.Id, indexer.Type);

        return indexer;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var indexer = await GetAsync(id, cancellationToken).ConfigureAwait(false);

        if (indexer is null)
        {
            return false;
        }

        _database.Indexers.Remove(indexer);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogDeleted(_logger, id);

        return true;
    }

    /// <inheritdoc />
    public async Task<ProviderTestResult> TestAsync(IndexerDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var (type, _) = await ValidateAsync(draft, cancellationToken).ConfigureAwait(false);
        var settings = await ResolveSettingsAsync(draft, type, cancellationToken).ConfigureAwait(false);

        try
        {
            return await type.TestAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A type that throws is a failed test, never a 500; the message is the type's own, and
            // the secret registry keeps it from naming a key.
            return new ProviderTestResult(false, exception.Message);
        }
    }

    /// <summary>
    /// Checks a draft and returns the type it names together with the protocol the row will store.
    /// </summary>
    private async Task<(IIndexerType Type, DownloadProtocol Protocol)> ValidateAsync(
        IndexerDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var type = _types.FirstOrDefault(candidate =>
            string.Equals(candidate.Type, draft.Type, StringComparison.OrdinalIgnoreCase))
            ?? throw new IndexerValidationException(
                "type",
                string.Concat("Unknown indexer type '", draft.Type, "'."));

        var name = draft.Name?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            throw new IndexerValidationException("name", "An indexer needs a name.");
        }

        // A type that serves both protocols leaves the choice to the row; the others fix it.
        var protocol = type.Protocol ?? draft.Protocol
            ?? throw new IndexerValidationException(
                "protocol",
                string.Concat("The '", type.Type, "' indexer type serves both protocols; choose Torrent or Usenet."));

        if (draft.DownloadClientId is { } clientId)
        {
            var client = await _database.DownloadClients
                .FirstOrDefaultAsync(candidate => candidate.Id == clientId, cancellationToken)
                .ConfigureAwait(false);

            if (client is null)
            {
                throw new IndexerValidationException(
                    "downloadClientId",
                    string.Concat("There is no download client with id ", clientId.ToString(), "."));
            }

            if (client.Protocol != protocol)
            {
                throw new IndexerValidationException(
                    "downloadClientId",
                    string.Concat(
                        "The download client '", client.Name, "' serves ", client.Protocol.ToString(),
                        ", but this indexer serves ", protocol.ToString(), "."));
            }
        }

        if (type.Validate(draft.Settings) is { Count: > 0 } failures)
        {
            throw new IndexerValidationException("settings", string.Join(" ", failures));
        }

        return (type, protocol);
    }

    /// <summary>
    /// The settings a test uses: the draft's own, with every masked secret replaced by the value
    /// stored on <paramref name="draft"/>'s row when it has one.
    /// </summary>
    private async Task<JsonElement> ResolveSettingsAsync(
        IndexerDraft draft,
        IIndexerType type,
        CancellationToken cancellationToken)
    {
        if (draft.Id is not { } id)
        {
            return draft.Settings;
        }

        var stored = await _database.Indexers
            .FirstOrDefaultAsync(indexer => indexer.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (stored is null)
        {
            return draft.Settings;
        }

        var merged = NotificationSecrets.Merge(draft.Settings, stored.Settings, type.Fields);

        return NotificationSecrets.Read(merged);
    }

    /// <summary>Tells the log pipeline about every secret value the row stores, so it can redact them.</summary>
    /// <param name="indexer">The row just read or written.</param>
    private void RegisterSecrets(Indexer indexer)
    {
        var type = _types.FirstOrDefault(candidate =>
            string.Equals(candidate.Type, indexer.Type, StringComparison.OrdinalIgnoreCase));

        if (type is null)
        {
            return;
        }

        var settings = NotificationSecrets.Read(indexer.Settings);

        foreach (var field in type.Fields)
        {
            if (field.Secret
                && settings.TryGetProperty(field.Name, out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } secret)
            {
                _secrets.Register(secret);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Saved indexer {Id} ({Type})")]
    private static partial void LogSaved(ILogger logger, long id, string type);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted indexer {Id}")]
    private static partial void LogDeleted(ILogger logger, long id);
}
