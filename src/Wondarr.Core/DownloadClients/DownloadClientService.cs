using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Logging;
using Wondarr.Core.Notifications;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Wondarr.Core.DownloadClients;

/// <summary>The values a create or an update carries.</summary>
/// <param name="Name">The display name.</param>
/// <param name="Type">The client type's name.</param>
/// <param name="Enabled">Whether grabs are sent to this client.</param>
/// <param name="Priority">The pick order; a lower number is picked first.</param>
/// <param name="Settings">The client type's settings.</param>
/// <param name="Id">The row a test is about, so a masked secret can be kept from it, or <see langword="null"/>.</param>
public sealed record DownloadClientDraft(
    string Name,
    string Type,
    bool Enabled,
    int Priority,
    JsonElement Settings,
    long? Id = null);

/// <summary>
/// A download client could not be stored: the values the user sent break one of the rules the API
/// reports as a <c>400</c>.
/// </summary>
public sealed class DownloadClientValidationException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="DownloadClientValidationException"/> class.</summary>
    /// <param name="field">The resource field the message belongs to, for example <c>type</c>.</param>
    /// <param name="detail">The sentence to show the user.</param>
    public DownloadClientValidationException(string field, string detail)
        : base(detail) => Field = field;

    /// <summary>Gets the field the message belongs to.</summary>
    public string Field { get; }

    /// <summary>Gets the sentence to show the user.</summary>
    public string Detail => Message;
}

/// <summary>
/// A download client is still named by indexers, so deleting it would leave them pointing at
/// nothing. The API answers a <c>409</c> naming them.
/// </summary>
public sealed class DownloadClientInUseException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="DownloadClientInUseException"/> class.</summary>
    /// <param name="indexerNames">The names of the indexers that name this client.</param>
    public DownloadClientInUseException(IReadOnlyList<string> indexerNames)
        : base(string.Concat(
            "The download client is still used by ",
            string.Join(", ", indexerNames.Select(name => string.Concat("'", name, "'"))),
            ". Delete or re-point the indexer(s) first."))
        => IndexerNames = indexerNames;

    /// <summary>Gets the names of the indexers that name this client.</summary>
    public IReadOnlyList<string> IndexerNames { get; }
}

/// <summary>The stored download clients the API reads and writes.</summary>
public interface IDownloadClientService
{
    /// <summary>Lists every download client, ordered by id.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlyList<DownloadClient>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Reads one download client, or <see langword="null"/>.</summary>
    /// <param name="id">The download client id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<DownloadClient?> GetAsync(long id, CancellationToken cancellationToken);

    /// <summary>Stores a new download client.</summary>
    /// <param name="draft">The values to store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<DownloadClient> CreateAsync(DownloadClientDraft draft, CancellationToken cancellationToken);

    /// <summary>Replaces a download client, or returns <see langword="null"/> when there is no such row.</summary>
    /// <param name="id">The download client id.</param>
    /// <param name="draft">The values to store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<DownloadClient?> UpdateAsync(long id, DownloadClientDraft draft, CancellationToken cancellationToken);

    /// <summary>Deletes a download client, returning whether there was one.</summary>
    /// <param name="id">The download client id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken);

    /// <summary>Connects with the draft's settings and reports whether the far end answered.</summary>
    /// <param name="draft">The download client to test.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    Task<ProviderTestResult> TestAsync(DownloadClientDraft draft, CancellationToken cancellationToken);
}

/// <summary>
/// The rules a download client is stored under: the type exists, the name is not blank, and the type
/// accepts the settings. Deleting one an indexer names is refused with the indexers' names.
/// </summary>
public sealed partial class DownloadClientService : IDownloadClientService
{
    private readonly WondarrDbContext _database;
    private readonly IReadOnlyList<IDownloadClientType> _types;
    private readonly ISecretRegistry _secrets;
    private readonly ILogger<DownloadClientService> _logger;

    /// <summary>Initialises a new instance of the <see cref="DownloadClientService"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="types">Every registered download client type.</param>
    /// <param name="secrets">Where secret settings are registered so the log pipeline can redact them.</param>
    /// <param name="logger">The log sink.</param>
    public DownloadClientService(
        WondarrDbContext database,
        IEnumerable<IDownloadClientType> types,
        ISecretRegistry secrets,
        ILogger<DownloadClientService> logger)
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
    public async Task<IReadOnlyList<DownloadClient>> ListAsync(CancellationToken cancellationToken)
    {
        var clients = await _database.DownloadClients
            .OrderBy(client => client.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var client in clients)
        {
            RegisterSecrets(client);
        }

        return clients;
    }

    /// <inheritdoc />
    public async Task<DownloadClient?> GetAsync(long id, CancellationToken cancellationToken)
    {
        var client = await _database.DownloadClients
            .FirstOrDefaultAsync(client => client.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (client is not null)
        {
            RegisterSecrets(client);
        }

        return client;
    }

    /// <inheritdoc />
    public async Task<DownloadClient> CreateAsync(DownloadClientDraft draft, CancellationToken cancellationToken)
    {
        var type = Validate(draft);

        var client = new DownloadClient
        {
            Name = draft.Name.Trim(),
            Type = type.Type,
            Protocol = type.Protocol,
            Enabled = draft.Enabled,
            Priority = draft.Priority,
            Settings = NotificationSecrets.Merge(draft.Settings, null, type.Fields),
        };

        RegisterSecrets(client);

        _database.DownloadClients.Add(client);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogSaved(_logger, client.Id, client.Type);

        return client;
    }

    /// <inheritdoc />
    public async Task<DownloadClient?> UpdateAsync(
        long id,
        DownloadClientDraft draft,
        CancellationToken cancellationToken)
    {
        var client = await GetAsync(id, cancellationToken).ConfigureAwait(false);

        if (client is null)
        {
            return null;
        }

        var type = Validate(draft);

        client.Name = draft.Name.Trim();
        client.Type = type.Type;
        client.Protocol = type.Protocol;
        client.Enabled = draft.Enabled;
        client.Priority = draft.Priority;
        client.Settings = NotificationSecrets.Merge(draft.Settings, client.Settings, type.Fields);

        RegisterSecrets(client);

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogSaved(_logger, client.Id, client.Type);

        return client;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var client = await GetAsync(id, cancellationToken).ConfigureAwait(false);

        if (client is null)
        {
            return false;
        }

        var users = await _database.Indexers
            .Where(indexer => indexer.DownloadClientId == id)
            .Select(indexer => indexer.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (users.Count > 0)
        {
            throw new DownloadClientInUseException(users);
        }

        _database.DownloadClients.Remove(client);

        try
        {
            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // An indexer naming this client was saved between the check above and the delete; the
            // foreign key refused it. Same answer as the check gives.
            _database.ChangeTracker.Clear();

            var latecomers = await _database.Indexers
                .Where(indexer => indexer.DownloadClientId == id)
                .Select(indexer => indexer.Name)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            throw new DownloadClientInUseException(latecomers);
        }

        LogDeleted(_logger, id);

        return true;
    }

    /// <inheritdoc />
    public async Task<ProviderTestResult> TestAsync(DownloadClientDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var type = Validate(draft);
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
            // the secret registry keeps it from naming a password.
            return new ProviderTestResult(false, exception.Message);
        }
    }

    /// <summary>Checks a draft and returns the client type it names.</summary>
    private IDownloadClientType Validate(DownloadClientDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var type = _types.FirstOrDefault(candidate =>
            string.Equals(candidate.Type, draft.Type, StringComparison.OrdinalIgnoreCase))
            ?? throw new DownloadClientValidationException(
                "type",
                string.Concat("Unknown download client type '", draft.Type, "'."));

        var name = draft.Name?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            throw new DownloadClientValidationException("name", "A download client needs a name.");
        }

        if (type.Validate(draft.Settings) is { Count: > 0 } failures)
        {
            throw new DownloadClientValidationException("settings", string.Join(" ", failures));
        }

        return type;
    }

    /// <summary>
    /// The settings a test uses: the draft's own, with every masked secret replaced by the value
    /// stored on <paramref name="draft"/>'s row when it has one.
    /// </summary>
    private async Task<JsonElement> ResolveSettingsAsync(
        DownloadClientDraft draft,
        IDownloadClientType type,
        CancellationToken cancellationToken)
    {
        if (draft.Id is not { } id)
        {
            return draft.Settings;
        }

        var stored = await _database.DownloadClients
            .FirstOrDefaultAsync(client => client.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (stored is null)
        {
            return draft.Settings;
        }

        var merged = NotificationSecrets.Merge(draft.Settings, stored.Settings, type.Fields);

        return NotificationSecrets.Read(merged);
    }

    /// <summary>Tells the log pipeline about every secret value the row stores, so it can redact them.</summary>
    /// <param name="client">The row just read or written.</param>
    private void RegisterSecrets(DownloadClient client)
    {
        var type = _types.FirstOrDefault(candidate =>
            string.Equals(candidate.Type, client.Type, StringComparison.OrdinalIgnoreCase));

        if (type is null)
        {
            return;
        }

        var settings = NotificationSecrets.Read(client.Settings);

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Saved download client {Id} ({Type})")]
    private static partial void LogSaved(ILogger logger, long id, string type);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted download client {Id}")]
    private static partial void LogDeleted(ILogger logger, long id);
}
