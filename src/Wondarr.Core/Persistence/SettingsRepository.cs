using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Wondarr.Core.Persistence;

/// <inheritdoc />
public sealed class SettingsRepository : ISettingsRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly WondarrDbContext _context;

    /// <summary>Initialises a new instance of the <see cref="SettingsRepository"/> class.</summary>
    public SettingsRepository(WondarrDbContext context) => _context = context;

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var value = await _context.Settings
            .AsNoTracking()
            .Where(setting => setting.Key == key)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return value is null ? default : JsonSerializer.Deserialize<T>(value, SerializerOptions);
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var json = JsonSerializer.Serialize(value, SerializerOptions);

        var setting = await _context.Settings
            .FirstOrDefaultAsync(row => row.Key == key, cancellationToken)
            .ConfigureAwait(false);

        if (setting is null)
        {
            _context.Settings.Add(new Setting { Key = key, Value = json });
        }
        else
        {
            setting.Value = json;
        }

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var setting = await _context.Settings
            .FirstOrDefaultAsync(row => row.Key == key, cancellationToken)
            .ConfigureAwait(false);

        if (setting is null)
        {
            return false;
        }

        _context.Settings.Remove(setting);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
