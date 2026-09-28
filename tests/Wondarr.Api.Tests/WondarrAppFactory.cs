using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.RepresentationModel;

namespace Wondarr.Api.Tests;

/// <summary>
/// Hosts the API against its own temporary config directory and removes it afterwards.
/// <para>
/// Settings are written into this instance's <c>config.yml</c> and also applied with
/// <c>UseSetting</c>, so they are visible to <c>Program.cs</c> before the host is built no matter
/// which of the two sources wins. Keys are the configuration keys, for example
/// <c>Server:UrlBase</c>. <c>ConfigDir</c> is passed with <c>UseSetting</c> only.
/// </para>
/// </summary>
public sealed class WondarrAppFactory : WebApplicationFactory<Program>
{
    /// <summary>Configuration key → key in the <c>server</c> section of <c>config.yml</c>.</summary>
    private static readonly Dictionary<string, string> ServerYamlKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Server:UrlBase"] = "url_base",
        ["Server:Auth"] = "auth",
        ["Server:AuthRequired"] = "auth_required",
        ["Server:Port"] = "port",
        ["Server:BindAddress"] = "bind_address",
    };

    private readonly IReadOnlyDictionary<string, string?> _settings;
    private readonly Action<IServiceCollection>? _configureServices;

    /// <param name="settings">Configuration keys (for example <c>Server:UrlBase</c>) to apply.</param>
    /// <param name="configureServices">
    /// Extra registrations, appended after the app's own — used to swap in fake health checks.
    /// </param>
    public WondarrAppFactory(
        IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null)
    {
        _settings = settings ?? new Dictionary<string, string?>();
        _configureServices = configureServices;
        ConfigDir = Path.Combine(Path.GetTempPath(), "wondarr-api-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ConfigDir);

        ApiKey = _settings.TryGetValue("Server:ApiKey", out var apiKey) && !string.IsNullOrWhiteSpace(apiKey)
            ? apiKey
            : Guid.NewGuid().ToString("N");

        WriteConfigFile();
    }

    /// <summary>The temporary config directory (config.yml, wondarr.db, logs).</summary>
    public string ConfigDir { get; }

    private bool _disposing;

    /// <summary>The API key this instance is configured with.</summary>
    public string ApiKey { get; }

    /// <summary>Creates a client that reports the remote address in <c>X-Test-Remote-Ip</c>.</summary>
    public HttpClient CreateClient(string remoteIpAddress, bool allowAutoRedirect = false) =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = allowAutoRedirect }, remoteIpAddress);

    /// <summary>Creates a client with the given options, reporting the remote address in <c>X-Test-Remote-Ip</c>.</summary>
    public HttpClient CreateClient(WebApplicationFactoryClientOptions options, string remoteIpAddress)
    {
        var client = base.CreateClient(options);
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, remoteIpAddress);

        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConfigDir", ConfigDir);

        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter, RemoteIpStartupFilter>();

            _configureServices?.Invoke(services);
        });
    }

    protected override void Dispose(bool disposing)
    {
        // WebApplicationFactory.Dispose(true) runs DisposeAsync, which calls Dispose(true) again while
        // the host is still shutting down: the inner call would delete the config folder with the log
        // file still open (IOException, a few percent of runs). Only the outer call cleans up, after
        // the host is gone.
        if (_disposing)
        {
            return;
        }

        _disposing = true;
        base.Dispose(disposing);

        if (disposing)
        {
            // Pooled SQLite connections keep wondarr.db open on Windows until the pool is cleared.
            SqliteConnection.ClearPool(new SqliteConnection($"Data Source={Path.Combine(ConfigDir, "wondarr.db")}"));

            DeleteConfigDir();
        }
    }

    /// <summary>
    /// The host's rolling log file can stay open for a moment after the host is disposed, so a
    /// Windows delete may briefly fail. Retry, and in the end leave the temp folder behind rather
    /// than fail a test that passed.
    /// </summary>
    private void DeleteConfigDir()
    {
        for (var attempt = 0; attempt < 20 && Directory.Exists(ConfigDir); attempt++)
        {
            try
            {
                Directory.Delete(ConfigDir, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// Writes <c>config.yml</c> with the requested server settings, and an API key, so
    /// <c>ConfigFileInitializer</c> leaves the file alone.
    /// </summary>
    private void WriteConfigFile()
    {
        var server = new YamlMappingNode { { "api_key", new YamlScalarNode(ApiKey) } };

        foreach (var (key, value) in _settings)
        {
            if (value is not null && ServerYamlKeys.TryGetValue(key, out var yamlKey))
            {
                server.Children[new YamlScalarNode(yamlKey)] = new YamlScalarNode(value);
            }
        }

        // The Soulseek storage health checks probe these directories for real, so they point into the
        // temporary config directory: a test run must not depend on, or create, /data.
        var soulseek = new YamlMappingNode
        {
            { "downloads_dir", new YamlScalarNode(Path.Combine(ConfigDir, "downloads", "slskd")) },
            { "incomplete_dir", new YamlScalarNode(Path.Combine(ConfigDir, "downloads", "slskd", "incomplete")) },
        };

        var document = new YamlStream(new YamlDocument(new YamlMappingNode
        {
            { "server", server },
            { "soulseek", soulseek },
        }));

        using var writer = new StreamWriter(
            Path.Combine(ConfigDir, "config.yml"),
            append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        document.Save(writer, assignAnchors: false);
    }
}
