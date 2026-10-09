using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Wondarr.Core.CustomFilters;

/// <summary>The values a create or an update carries.</summary>
/// <param name="Type">The page the view belongs to, for example <c>library</c>.</param>
/// <param name="Label">The view's name.</param>
/// <param name="Filters">The filters: a JSON array of objects, each with a string <c>key</c>.</param>
public sealed record CustomFilterDraft(string? Type, string? Label, JsonElement Filters);

/// <summary>The draft is unusable.</summary>
public sealed class CustomFilterValidationException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="CustomFilterValidationException"/> class.</summary>
    /// <param name="field">The field at fault.</param>
    /// <param name="detail">What is wrong with it.</param>
    public CustomFilterValidationException(string field, string detail)
        : base(detail)
    {
        Field = field;
        Detail = detail;
    }

    /// <summary>Gets the field at fault.</summary>
    public string Field { get; }

    /// <summary>Gets what is wrong with it.</summary>
    public string Detail { get; }
}

/// <summary>The label is already taken by another view of the same type.</summary>
public sealed class CustomFilterConflictException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="CustomFilterConflictException"/> class.</summary>
    /// <param name="type">The view type.</param>
    /// <param name="label">The label.</param>
    public CustomFilterConflictException(string type, string label)
        : base($"A '{type}' view called '{label}' already exists.")
    {
    }
}

/// <summary>The stored saved views.</summary>
public interface ICustomFilterService
{
    /// <summary>Lists the views, optionally of one type, ordered by type then label.</summary>
    /// <param name="type">Only views of this type, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlyList<CustomFilter>> ListAsync(string? type, CancellationToken cancellationToken);

    /// <summary>Reads one view, or <see langword="null"/>.</summary>
    /// <param name="id">The view id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<CustomFilter?> GetAsync(long id, CancellationToken cancellationToken);

    /// <summary>Stores a new view.</summary>
    /// <param name="draft">The values.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="CustomFilterValidationException">The draft is invalid.</exception>
    /// <exception cref="CustomFilterConflictException">The label is taken.</exception>
    Task<CustomFilter> CreateAsync(CustomFilterDraft draft, CancellationToken cancellationToken);

    /// <summary>Replaces a view, or returns <see langword="null"/> when there is no such row.</summary>
    /// <param name="id">The view id.</param>
    /// <param name="draft">The values.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="CustomFilterValidationException">The draft is invalid.</exception>
    /// <exception cref="CustomFilterConflictException">The label is taken.</exception>
    Task<CustomFilter?> UpdateAsync(long id, CustomFilterDraft draft, CancellationToken cancellationToken);

    /// <summary>Deletes a view, returning whether there was one.</summary>
    /// <param name="id">The view id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class CustomFilterService : ICustomFilterService
{
    /// <summary>The longest label, in characters.</summary>
    public const int MaxLabelLength = 100;

    /// <summary>The longest type, in characters.</summary>
    public const int MaxTypeLength = 50;

    /// <summary>The most filters one view holds.</summary>
    public const int MaxFilters = 20;

    /// <summary>SQLite's extended result code for a broken UNIQUE constraint (<c>SQLITE_CONSTRAINT_UNIQUE</c>).</summary>
    private const int UniqueConstraintFailed = 2067;

    /// <summary>The largest filters JSON, in bytes.</summary>
    public const int MaxFiltersBytes = 8 * 1024;

    private readonly WondarrDbContext _database;

    /// <summary>Initialises a new instance of the <see cref="CustomFilterService"/> class.</summary>
    /// <param name="database">The database.</param>
    public CustomFilterService(WondarrDbContext database)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CustomFilter>> ListAsync(string? type, CancellationToken cancellationToken)
    {
        var query = _database.CustomFilters.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(type))
        {
            var wanted = type.Trim();
            query = query.Where(filter => filter.Type == wanted);
        }

        return await query
            .OrderBy(filter => filter.Type)
            .ThenBy(filter => filter.Label)
            .ThenBy(filter => filter.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CustomFilter?> GetAsync(long id, CancellationToken cancellationToken) =>
        await _database.CustomFilters
            .AsNoTracking()
            .FirstOrDefaultAsync(filter => filter.Id == id, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<CustomFilter> CreateAsync(CustomFilterDraft draft, CancellationToken cancellationToken)
    {
        var (type, label, filters) = Validate(draft);
        await RequireFreeLabelAsync(type, label, null, cancellationToken).ConfigureAwait(false);

        var row = new CustomFilter { Type = type, Label = label, Filters = filters };
        _database.CustomFilters.Add(row);
        await SaveAsync(row, cancellationToken).ConfigureAwait(false);

        return row;
    }

    /// <inheritdoc />
    public async Task<CustomFilter?> UpdateAsync(long id, CustomFilterDraft draft, CancellationToken cancellationToken)
    {
        var (type, label, filters) = Validate(draft);

        var row = await _database.CustomFilters
            .FirstOrDefaultAsync(filter => filter.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        await RequireFreeLabelAsync(type, label, id, cancellationToken).ConfigureAwait(false);

        row.Type = type;
        row.Label = label;
        row.Filters = filters;
        await SaveAsync(row, cancellationToken).ConfigureAwait(false);

        return row;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var row = await _database.CustomFilters
            .FirstOrDefaultAsync(filter => filter.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return false;
        }

        _database.CustomFilters.Remove(row);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    private static (string Type, string Label, string Filters) Validate(CustomFilterDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var type = draft.Type?.Trim() ?? string.Empty;
        if (type.Length == 0 || type.Length > MaxTypeLength)
        {
            throw new CustomFilterValidationException("type", $"type must be 1 to {MaxTypeLength} characters.");
        }

        var label = draft.Label?.Trim() ?? string.Empty;
        if (label.Length == 0 || label.Length > MaxLabelLength)
        {
            throw new CustomFilterValidationException("label", $"label must be 1 to {MaxLabelLength} characters.");
        }

        var filters = draft.Filters;
        if (filters.ValueKind != JsonValueKind.Array)
        {
            throw new CustomFilterValidationException("filters", "filters must be a JSON array.");
        }

        // The count first: a huge array is refused before any entry is looked at.
        if (filters.GetArrayLength() > MaxFilters)
        {
            throw new CustomFilterValidationException("filters", $"filters holds more than {MaxFilters} entries.");
        }

        foreach (var entry in filters.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("key", out var key)
                || key.ValueKind != JsonValueKind.String)
            {
                throw new CustomFilterValidationException(
                    "filters",
                    "every filter must be an object with a string key.");
            }
        }

        var raw = filters.GetRawText();
        if (System.Text.Encoding.UTF8.GetByteCount(raw) > MaxFiltersBytes)
        {
            throw new CustomFilterValidationException("filters", $"filters is larger than {MaxFiltersBytes / 1024} KB.");
        }

        return (type, label, raw);
    }

    private async Task RequireFreeLabelAsync(string type, string label, long? ownId, CancellationToken cancellationToken)
    {
        if (await _database.CustomFilters
            .AnyAsync(filter => filter.Type == type && filter.Label == label && filter.Id != ownId, cancellationToken)
            .ConfigureAwait(false))
        {
            throw new CustomFilterConflictException(type, label);
        }
    }

    private async Task SaveAsync(CustomFilter row, CancellationToken cancellationToken)
    {
        try
        {
            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteExtendedErrorCode: UniqueConstraintFailed })
        {
            // A racing request took the label between the check and the write: the unique index says so.
            throw new CustomFilterConflictException(row.Type, row.Label);
        }
    }
}
