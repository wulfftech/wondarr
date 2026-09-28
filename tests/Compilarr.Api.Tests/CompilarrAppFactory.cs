using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Compilarr.Api.Tests;

/// <summary>
/// Hosts the API against its own temporary config directory and removes it afterwards.
/// Settings passed in <paramref name="settings"/> are applied with <c>UseSetting</c>, so they
/// are visible to <c>Program.cs</c> before the host is built (for example <c>Server:UrlBase</c>).
/// </summary>
public sealed class CompilarrAppFactory : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string?> _settings;

    public CompilarrAppFactory(IReadOnlyDictionary<string, string?>? settings = null)
    {
        _settings = settings ?? new Dictionary<string, string?>();
        ConfigDir = Path.Combine(Path.GetTempPath(), "compilarr-api-tests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>The temporary config directory (config.yml, compilarr.db, logs).</summary>
    public string ConfigDir { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConfigDir", ConfigDir);

        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            // Pooled SQLite connections keep compilarr.db open on Windows until the pool is cleared.
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(ConfigDir))
            {
                Directory.Delete(ConfigDir, recursive: true);
            }
        }
    }
}
