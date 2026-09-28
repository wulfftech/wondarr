namespace Wondarr.Core.Persistence;

/// <summary>
/// Typed key/value access to the <c>setting</c> table. Values are stored as JSON.
/// </summary>
public interface ISettingsRepository
{
    /// <summary>Reads a setting, returning <see langword="default"/> when the key is absent.</summary>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken);

    /// <summary>Inserts or updates a setting.</summary>
    Task SetAsync<T>(string key, T value, CancellationToken cancellationToken);

    /// <summary>Deletes a setting. Returns <see langword="true"/> when a row was removed.</summary>
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken);
}
